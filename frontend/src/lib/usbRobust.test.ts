import { afterEach, describe, expect, it, vi } from 'vitest'
import { awaitConsole, flashAttempts, type ConsoleStatus } from '@/lib/hornetSetup'
import { panelBusy } from '@/lib/remoteSetup'
import { HornetSerial, isPortBusy, type SerialPortLike } from '@/lib/serial'

/** A serial port that does what a board does: after a reset it says "ready" if it is awake, and it can refuse to open. */
class FakePort implements SerialPortLike {
  readable: ReadableStream<Uint8Array> | null = null
  writable: WritableStream<Uint8Array> | null = null
  opened = 0
  closed = 0
  signals: { dataTerminalReady?: boolean; requestToSend?: boolean }[] = []
  written: string[] = []
  openErrors: Error[] = []
  /** Says "[monitor] ready" only after this many resets (a board that missed its console window). */
  readyAfterResets = 0
  private controller?: ReadableStreamDefaultController<Uint8Array>
  private resets = 0
  private open_ = false

  async open() {
    const error = this.openErrors.shift()
    if (error) throw error
    if (this.open_) throw Object.assign(new Error('already open'), { name: 'InvalidStateError' })
    this.open_ = true
    this.opened++
    this.readable = new ReadableStream({ start: (c) => { this.controller = c } })
    this.writable = new WritableStream({ write: (chunk) => { this.written.push(new TextDecoder().decode(chunk)) } })
  }

  async close() {
    this.open_ = false
    this.closed++
  }

  async setSignals(s: { dataTerminalReady?: boolean; requestToSend?: boolean }) {
    this.signals.push(s)
    // RTS released after being held is the end of a reset pulse
    const n = this.signals.length
    if (n >= 2 && this.signals[n - 2].requestToSend && !s.requestToSend) {
      this.resets++
      if (this.resets >= this.readyAfterResets) this.say('[monitor] ready\n')
    }
  }

  say(text: string) {
    setTimeout(() => {
      try {
        this.controller?.enqueue(new TextEncoder().encode(text))
      } catch { /* the port was closed meanwhile: nobody hears it */ }
    }, 5)
  }
}

const quick = { waitMs: 60, nudgeMs: 10 }

afterEach(() => vi.useRealTimers())

describe('waiting for the console of a board that reset when the port opened', () => {
  it('resets the board by DTR/RTS and hears it after the first wait missed its window', async () => {
    const port = new FakePort()
    port.readyAfterResets = 1  // silent until it is reset
    const seen: ConsoleStatus[] = []
    const { board, line } = await awaitConsole({
      open: () => HornetSerial.attach(port, () => {}),
      ready: (l) => l === '[monitor] ready',
      onStatus: (s) => seen.push(s),
      ...quick,
    })
    expect(line).toBe('[monitor] ready')
    expect(seen.map((s) => `${s.step}${s.attempt}`)).toEqual(['listen1', 'reset2', 'listen2'])
    // RTS went low (EN), then high, with DTR (IO0) released all the time
    const pulse = port.signals.filter((s) => s.requestToSend)
    expect(pulse).toHaveLength(1)
    expect(port.signals.every((s) => s.dataTerminalReady === false)).toBe(true)
    expect(port.written.join('')).toContain('\n')  // nudged while it waited: a newline keeps a panel's console window open
    await board.close()
  })

  it('reopens the port on the third try and gives up after the last one without throwing', async () => {
    const port = new FakePort()
    port.readyAfterResets = 99  // never answers
    const seen: string[] = []
    const { board, line } = await awaitConsole({
      open: () => HornetSerial.attach(port, () => {}),
      ready: (l) => l === '[monitor] ready',
      onStatus: (s) => seen.push(`${s.step}${s.attempt}/${s.of}`),
      ...quick,
    })
    expect(line).toBeNull()
    expect(seen).toEqual(['listen1/4', 'reset2/4', 'listen2/4', 'reopen3/4', 'reset3/4', 'listen3/4', 'reset4/4', 'listen4/4'])
    expect(port.opened).toBe(2)  // first open and the reopen
    expect(port.closed).toBe(1)
    await board.close()
  })

  it('does not reset a board that already answers', async () => {
    const port = new FakePort()
    const hornet = await HornetSerial.attach(port, () => {})
    port.say('[monitor] ready\n')
    await hornet.close()
    port.signals = []
    const { board, line } = await awaitConsole({
      open: async () => {
        const b = await HornetSerial.attach(port, () => {})
        port.say('[monitor] ready\n')
        return b
      },
      ready: (l) => l === '[monitor] ready',
      ...quick,
    })
    expect(line).toBe('[monitor] ready')
    expect(port.signals.filter((s) => s.requestToSend)).toHaveLength(0)
    await board.close()
  })
})

describe('opening a port that is not free', () => {
  it('closes and opens again a port that is still open from an earlier try', async () => {
    const port = new FakePort()
    await port.open()  // left open
    const board = await HornetSerial.attach(port, () => {})
    expect(port.closed).toBe(1)
    expect(port.opened).toBe(2)
    await board.close()
  })

  it('tells a port held by another program from a gone one', () => {
    expect(isPortBusy(Object.assign(new Error('Failed to open serial port.'), { name: 'NetworkError' }))).toBe(true)
    expect(isPortBusy(new Error('Failed to open serial port'))).toBe(true)
    expect(isPortBusy(new Error('The device has been lost.'))).toBe(false)
    expect(isPortBusy(null)).toBe(false)
  })
})

describe('flashing: the connection is tried again, slower the last time', () => {
  it('has three tries and the last one is the slowest', () => {
    expect(flashAttempts).toHaveLength(3)
    expect(Math.min(...flashAttempts.map((a) => a.baud))).toBe(flashAttempts.at(-1)!.baud)
  })
})

describe('a board that is busy on its first start (formatting its flash) is waited for, not reset', () => {
  it('treats a busy line as a sign of life: the wait starts over and the board is not reset', async () => {
    const port = new FakePort()
    port.readyAfterResets = 99
    const seen: string[] = []
    const open = async () => {
      const b = await HornetSerial.attach(port, () => {})
      port.say('[monitor] boot fw=0.2.0\n')                 // at once
      setTimeout(() => port.say('[monitor] flash: first start, formatting the file system\n'), 40)
      setTimeout(() => port.say('[monitor] flash: still formatting\n'), 90)  // the board keeps saying it is busy
      setTimeout(() => port.say('[monitor] ready\n'), 130)   // after more than one 60 ms wait
      return b
    }
    const { board, line } = await awaitConsole({
      open, ready: (l) => l === '[monitor] ready', alive: panelBusy, onStatus: (s) => seen.push(s.step), waitMs: 60, nudgeMs: 10,
    })
    expect(line).toBe('[monitor] ready')
    expect(seen).toEqual(['listen'])                           // never a reset or a reopen
    expect(port.signals.some((s) => s.requestToSend)).toBe(false)
    await board.close()
  })

  it('without a busy line the silence still ends the wait and the board is reset', async () => {
    const port = new FakePort()
    port.readyAfterResets = 1
    const seen: string[] = []
    const { board, line } = await awaitConsole({
      open: () => HornetSerial.attach(port, () => {}), ready: (l) => l === '[monitor] ready', alive: panelBusy, onStatus: (s) => seen.push(s.step), ...quick,
    })
    expect(line).toBe('[monitor] ready')
    expect(seen).toContain('reset')
    await board.close()
  })

  it('listens longer on the first try after a flash', async () => {
    const port = new FakePort()
    port.readyAfterResets = 99
    const seen: string[] = []
    const open = async () => {
      const b = await HornetSerial.attach(port, () => {})
      setTimeout(() => port.say('[monitor] ready\n'), 150)    // later than the usual wait of 60 ms, inside the first one of 300 ms
      return b
    }
    const { board, line } = await awaitConsole({
      open, ready: (l) => l === '[monitor] ready', onStatus: (s) => seen.push(s.step), waitMs: 60, nudgeMs: 10, firstWaitMs: 300,
    })
    expect(line).toBe('[monitor] ready')
    expect(seen).toEqual(['listen'])
    await board.close()
  })

  it('knows which panel lines mean "busy, wait"', () => {
    expect(panelBusy('[monitor] boot fw=0.2.0+abc')).toBe(true)
    expect(panelBusy('[monitor] flash: first start, formatting the file system (up to 30 s), please wait')).toBe(true)
    expect(panelBusy('[monitor] ready')).toBe(false)
    expect(panelBusy('ESP-ROM:esp32s3-20210327')).toBe(false)
  })
})
