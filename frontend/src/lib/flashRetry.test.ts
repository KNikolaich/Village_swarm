import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { SerialPortLike } from '@/lib/serial'

// esptool-js played back: connecting fails a given number of times, then works; the writing can fail once.
const esp = vi.hoisted(() => ({
  connectFailures: 0, writeFailures: 0, chip: 'ESP32-S3', bauds: [] as number[], connects: 0, writes: 0, disconnects: 0, after: 0,
}))

vi.mock('esptool-js', () => ({
  Transport: class { async disconnect() { esp.disconnects++ } },
  ESPLoader: class {
    chip = { CHIP_NAME: esp.chip }
    constructor(o: { baudrate: number }) { esp.bauds.push(o.baudrate) }
    async main() {
      esp.connects++
      if (esp.connectFailures-- > 0) throw new Error('Failed to connect: no serial data received')
    }
    async writeFlash() {
      esp.writes++
      if (esp.writeFailures-- > 0) throw new Error('lost sync while writing')
    }
    async after() { esp.after++ }
  },
}))

const { flashBoard, SetupError } = await import('@/lib/hornetSetup')

const build = { build: 'monitor-epd154', chipFamily: 'ESP32-S3' }
const port = {} as SerialPortLike

function options() {
  const log: string[] = []
  return { log, o: { onLog: (l: string) => log.push(l), onProgress: () => {} } }
}

beforeEach(() => {
  Object.assign(esp, { connectFailures: 0, writeFailures: 0, chip: 'ESP32-S3', bauds: [], connects: 0, writes: 0, disconnects: 0, after: 0 })
  vi.stubGlobal('fetch', vi.fn(async (url: string) =>
    url.endsWith('manifest.json')
      ? { ok: true, json: async () => ({ builds: [{ chipFamily: 'ESP32-S3', parts: [{ path: '/fw/part.bin', offset: 0 }] }] }) }
      : { ok: true, arrayBuffer: async () => new Uint8Array(100).buffer }))
})

describe('flashing a board', () => {
  it('connects again after a failed try, on a fresh connection', async () => {
    esp.connectFailures = 1
    const { o, log } = options()
    await flashBoard(port, build, o)
    expect(esp.connects).toBe(2)
    expect(esp.writes).toBe(1)
    expect(esp.after).toBe(1)
    expect(esp.disconnects).toBe(2)  // every try lets go of the port
    expect(log.some((l) => l.includes('try 2 of 3'))).toBe(true)
  }, 15_000)

  it('writes again at a lower speed when the writing broke off', async () => {
    esp.writeFailures = 2
    const { o } = options()
    await flashBoard(port, build, o)
    expect(esp.bauds).toEqual([115200, 115200, 57600])
    expect(esp.writes).toBe(3)
  }, 15_000)

  it('says it never reached the bootloader after the last try', async () => {
    esp.connectFailures = 99
    const { o } = options()
    const error = await flashBoard(port, build, o).catch((e) => e)
    expect(error).toBeInstanceOf(SetupError)
    expect(error).toMatchObject({ stage: 'flash', reason: 'noBootloader' })
    expect(esp.connects).toBe(3)
  }, 15_000)

  it('says the writing failed when the bootloader answered but the writing never worked', async () => {
    esp.writeFailures = 99
    const { o } = options()
    const error = await flashBoard(port, build, o).catch((e) => e)
    expect(error).toMatchObject({ stage: 'flash', reason: 'flashFailed' })
  }, 15_000)

  it('does not try again with the wrong board on the desk', async () => {
    esp.chip = 'ESP32-C3'
    const { o } = options()
    const error = await flashBoard(port, build, o).catch((e) => e)
    expect(error).toMatchObject({ stage: 'flash', reason: 'wrongChip' })
    expect(esp.connects).toBe(1)
    expect(esp.writes).toBe(0)
  })
})
