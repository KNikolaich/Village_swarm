// One-click setup of a new hornet over USB: flash the firmware, wait for it to start, send the Wi-Fi and the one-time
// code, wait for "enrolled", then for the hornet to come online. One port pick for all of it (Web Serial, Chrome/Edge).
import { HornetSerial, type SerialPortLike } from '@/lib/serial'

export type SetupStage = 'port' | 'flash' | 'restart' | 'settings' | 'enroll' | 'online'

export const setupStages: SetupStage[] = ['port', 'flash', 'restart', 'settings', 'enroll', 'online']

/** Why the setup stopped: the stage, a reason key (i18n add.why.*) and details for the log. */
export class SetupError extends Error {
  constructor(readonly stage: SetupStage, readonly reason: string, detail?: string) {
    super(detail ?? reason)
  }
}

/** What a console line of the firmware means for the wizard (lib/hornet-core/src/HornetCore.cpp). */
export function lineMeaning(line: string): 'booted' | 'enrolled' | 'enrollFailed' | null {
  if (/^\[hornet\] \S+ fw=/.test(line)) return 'booted'
  if (line.startsWith('enrolled ')) return 'enrolled'
  if (line.startsWith('enroll failed')) return 'enrollFailed'
  return null
}

/** esptool names chips the way build.json names chip families ("ESP32-C3"); spelling aside, they must be the same. */
export function chipMatches(chipName: string, chipFamily: string) {
  const norm = (s: string) => s.toUpperCase().replace(/[^A-Z0-9]/g, '')
  return norm(chipName) === norm(chipFamily)
}

/** The bytes as the "binary string" esptool-js 0.5 takes. */
export function binaryString(bytes: Uint8Array) {
  let out = ''
  for (let i = 0; i < bytes.length; i += 0x8000) out += String.fromCharCode(...bytes.subarray(i, i + 0x8000))
  return out
}

type Manifest = { builds: { chipFamily: string; parts: { path: string; offset: number }[] }[] }

const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms))

export type SetupOptions = {
  port: SerialPortLike
  /** The firmware build to write first; undefined when the board is already flashed. */
  build?: { build: string; chipFamily: string }
  commands: string[]
  onStage: (stage: SetupStage) => void
  onProgress: (percent: number) => void
  onLog: (line: string) => void
  /** Whether the hive sees the hornet online (asked again and again after "enrolled"). */
  isOnline: () => Promise<boolean>
}

async function flash(port: SerialPortLike, build: { build: string; chipFamily: string }, o: SetupOptions) {
  const manifest = (await (await fetch(`/api/firmware/${build.build}/manifest.json`)).json()) as Manifest
  const parts = manifest.builds[0]?.parts ?? []
  const files = await Promise.all(parts.map(async (p) => {
    const response = await fetch(p.path)
    if (!response.ok) throw new SetupError('flash', 'download', `${p.path}: ${response.status}`)
    return { data: binaryString(new Uint8Array(await response.arrayBuffer())), address: p.offset }
  }))
  // ~150 KB of flasher stubs: loaded only when a board is flashed
  const { ESPLoader, Transport } = await import('esptool-js')
  const transport = new Transport(port as unknown as SerialPort, false)
  try {
    const loader = new ESPLoader({
      transport, baudrate: 115200, romBaudrate: 115200,
      terminal: { clean: () => {}, write: () => {}, writeLine: (line: string) => o.onLog(line) },
    })
    try {
      await loader.main()
    } catch (e) {
      throw new SetupError('flash', 'noBootloader', String(e))
    }
    const chip = loader.chip.CHIP_NAME
    if (!chipMatches(chip, build.chipFamily)) throw new SetupError('flash', 'wrongChip', `${chip} ≠ ${build.chipFamily}`)
    const total = files.reduce((sum, f) => sum + f.data.length, 0)
    const before = files.map((_, i) => files.slice(0, i).reduce((sum, f) => sum + f.data.length, 0))
    await loader.writeFlash({
      fileArray: files, flashSize: 'keep', flashMode: 'keep', flashFreq: 'keep', eraseAll: false, compress: true,
      reportProgress: (index, written, size) => o.onProgress(Math.round((100 * (before[index] + (written / size) * files[index].data.length)) / total)),
    })
    await loader.after('hard_reset')
  } finally {
    await transport.disconnect().catch(() => {})
  }
}

/**
 * The port again after the board restarted. A board with its own USB (ESP32-C3/S3) leaves and comes back as a new port:
 * the browser hands it over without asking, since the user allowed that device already.
 */
async function reopen(port: SerialPortLike, onLine: (line: string) => void): Promise<HornetSerial> {
  const serial = (navigator as unknown as { serial: { getPorts(): Promise<SerialPort[]> } }).serial
  const info = (port as unknown as SerialPort).getInfo?.() ?? {}
  for (let attempt = 0; attempt < 20; attempt++) {
    const candidates: SerialPortLike[] = [port, ...(await serial.getPorts()).filter((p) => {
      const i = p.getInfo()
      return p !== (port as unknown) && i.usbVendorId === info.usbVendorId && i.usbProductId === info.usbProductId
    })]
    for (const candidate of candidates) {
      try {
        return await HornetSerial.attach(candidate, onLine)
      } catch { /* not back yet */ }
    }
    await sleep(500)
  }
  throw new SetupError('restart', 'portGone')
}

/** Runs the whole setup; resolves when the hornet is online, throws SetupError when a stage fails. */
export async function setupHornet(o: SetupOptions) {
  let board: HornetSerial | null = null
  try {
    if (o.build) {
      o.onStage('flash')
      await flash(o.port, o.build, o)
    }
    o.onStage('restart')
    board = await reopen(o.port, o.onLog)
    // The banner of the firmware; a board that booted before the port opened says nothing, so do not wait forever.
    const booted = board.waitForLine((l) => lineMeaning(l) === 'booted', 8000)
    for (let i = 0; i < 16 && !(await Promise.race([booted, sleep(500).then(() => false)])); i++)
      await board.send('').catch(() => {})  // a newline keeps a battery hornet awake for the console
    o.onStage('settings')
    const result = board.waitForLine((l) => lineMeaning(l) === 'enrolled' || lineMeaning(l) === 'enrollFailed', 45_000)
    for (const command of o.commands) {
      await board.send(command)
      await sleep(300)
    }
    o.onStage('enroll')
    const answer = await result
    if (answer === null) throw new SetupError('enroll', 'noAnswer')
    if (lineMeaning(answer) === 'enrollFailed') throw new SetupError('enroll', 'enrollFailed', answer)
    o.onStage('online')
    for (let i = 0; i < 45; i++) {
      if (await o.isOnline().catch(() => false)) return
      await sleep(2000)
    }
    throw new SetupError('online', 'notOnline')
  } finally {
    await board?.close()
  }
}
