// One-click setup of a new hornet over USB: flash the firmware, wait for it to start, send the Wi-Fi and the one-time
// code, wait for "enrolled", then for the hornet to come online. One port pick for all of it (Web Serial, Chrome/Edge).
import { HornetSerial, isPortBusy, type SerialPortLike } from '@/lib/serial'

export type SetupStage = 'port' | 'flash' | 'restart' | 'settings' | 'enroll' | 'online' | 'read'

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

export const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms))

export type SetupOptions = {
  port: SerialPortLike
  /** The firmware build to write first; undefined when the board is already flashed. */
  build?: { build: string; chipFamily: string }
  commands: string[]
  onStage: (stage: SetupStage) => void
  onProgress: (percent: number) => void
  onLog: (line: string) => void
  /** What the page does while it waits for the board (listening, resetting, reopening the port). */
  onStatus?: (status: ConsoleStatus) => void
  /** Whether the hive sees the hornet online (asked again and again after "enrolled"). */
  isOnline: () => Promise<boolean>
}

/** Writes a firmware build of the hive to the board on this port and restarts it. */
export async function flashBoard(port: SerialPortLike, build: { build: string; chipFamily: string }, o: Pick<SetupOptions, 'onLog' | 'onProgress'>) {
  // Never from the browser's cache: after a deploy the same address carries a new image.
  const manifest = (await (await fetch(`/api/firmware/${build.build}/manifest.json`, { cache: 'no-store' })).json()) as Manifest
  const parts = manifest.builds[0]?.parts ?? []
  const files = await Promise.all(parts.map(async (p) => {
    const response = await fetch(p.path, { cache: 'no-store' })
    if (!response.ok) throw new SetupError('flash', 'download', `${p.path}: ${response.status}`)
    return { data: binaryString(new Uint8Array(await response.arrayBuffer())), address: p.offset }
  }))
  // ~150 KB of flasher stubs: loaded only when a board is flashed
  const { ESPLoader, Transport } = await import('esptool-js')
  const total = files.reduce((sum, f) => sum + f.data.length, 0)
  const before = files.map((_, i) => files.slice(0, i).reduce((sum, f) => sum + f.data.length, 0))
  let lastError: unknown = null
  let connected = false
  for (const [index, attempt] of flashAttempts.entries()) {
    if (index > 0) {
      o.onLog(`flash: try ${index + 1} of ${flashAttempts.length} at ${attempt.baud} baud`)
      await sleep(600)  // the board and the adapter settle after the failed try
    }
    const transport = new Transport(port as unknown as SerialPort, false)
    try {
      const loader = new ESPLoader({
        transport, baudrate: attempt.baud, romBaudrate: 115200,
        terminal: { clean: () => {}, write: () => {}, writeLine: (line: string) => o.onLog(line) },
      })
      try {
        await loader.main()
        connected = true
      } catch (e) {
        throw new SetupError('flash', 'noBootloader', String(e))
      }
      const chip = loader.chip.CHIP_NAME
      // a wrong board is no flaky cable: no use trying again
      if (!chipMatches(chip, build.chipFamily)) throw new FatalSetupError('flash', 'wrongChip', `${chip} ≠ ${build.chipFamily}`)
      await loader.writeFlash({
        fileArray: files, flashSize: 'keep', flashMode: 'keep', flashFreq: 'keep', eraseAll: false, compress: true,
        reportProgress: (i, written, size) => o.onProgress(Math.round((100 * (before[i] + (written / size) * files[i].data.length)) / total)),
      })
      await loader.after('hard_reset')
      return
    } catch (e) {
      if (e instanceof FatalSetupError) throw e
      lastError = e
      o.onLog(`flash: ${e instanceof Error ? e.message : String(e)}`)
    } finally {
      await transport.disconnect().catch(() => {})
    }
  }
  // never reached the bootloader: cable, port, the board's boot mode; reached it but the writing broke: the cable or power
  throw new SetupError('flash', connected ? 'flashFailed' : 'noBootloader', String(lastError))
}

/** A failure that another try cannot mend. */
class FatalSetupError extends SetupError {}

/**
 * How a flash is tried: the proven speed twice (the second after the board and the adapter settled, on a fresh connection),
 * then slower, which gets through a long or poor cable. The writing is repeatable: a failed one is simply done again.
 */
export const flashAttempts: { baud: number }[] = [{ baud: 115200 }, { baud: 115200 }, { baud: 57600 }]

/**
 * The port again after the board restarted. A board with its own USB (ESP32-C3/S3) leaves and comes back as a new port:
 * the browser hands it over without asking, since the user allowed that device already.
 */
export async function reopenConsole(port: SerialPortLike, onLine: (line: string) => void): Promise<HornetSerial> {
  const serial = (navigator as unknown as { serial: { getPorts(): Promise<SerialPort[]> } }).serial
  const info = (port as unknown as SerialPort).getInfo?.() ?? {}
  let lastError: unknown = null
  for (let attempt = 0; attempt < 20; attempt++) {
    const candidates: SerialPortLike[] = [port, ...(await serial.getPorts()).filter((p) => {
      const i = p.getInfo()
      return p !== (port as unknown) && i.usbVendorId === info.usbVendorId && i.usbProductId === info.usbProductId
    })]
    for (const candidate of candidates) {
      try {
        return await HornetSerial.attach(candidate, onLine)
      } catch (e) {
        lastError = e  // not back yet, or held by another program
      }
    }
    await sleep(500)
  }
  throw new SetupError('restart', lastError !== null && isPortBusy(lastError) ? 'portBusy' : 'portGone', lastError === null ? undefined : String(lastError))
}

/** What the page is doing while it waits for the board's console (shown to the person). */
export type ConsoleStatus = { step: 'listen' | 'reset' | 'reopen'; attempt: number; of: number }

export type AwaitConsoleOptions = {
  /** Opens the console on the port (again): reopenConsole. Called first, and again when the port is reopened. */
  open: () => Promise<HornetSerial>
  /** The line that says the board is up and listening. */
  ready: (line: string) => boolean
  onStatus?: (status: ConsoleStatus) => void
  /** Tries in all: 1 just listens, 2 resets the board by DTR/RTS, 3 reopens the port and resets, 4 resets once more. */
  attempts?: number
  /** How long each try listens. Every newline sent meanwhile keeps a panel's console window open (it lasts only 12 s after power-on). */
  waitMs?: number
  nudgeMs?: number
}

/**
 * Waits for the board to say it is ready. A board resets when the port is opened and its console window is short, so one
 * wait often misses it: this listens, nudges, resets the board through DTR/RTS, reopens the port, and listens again.
 * Returns the open console and the line that matched, or null for the line when nothing came after every try.
 */
export async function awaitConsole(o: AwaitConsoleOptions): Promise<{ board: HornetSerial; line: string | null }> {
  const attempts = o.attempts ?? 4
  const waitMs = o.waitMs ?? 12_000
  const nudgeMs = o.nudgeMs ?? 700
  let board = await o.open()
  for (let attempt = 1; attempt <= attempts; attempt++) {
    if (attempt >= 2) {
      if (attempt === 3) {
        o.onStatus?.({ step: 'reopen', attempt, of: attempts })
        await board.close()
        await sleep(400)
        board = await o.open()
      }
      o.onStatus?.({ step: 'reset', attempt, of: attempts })
      await board.pulseReset().catch(() => {})
    }
    o.onStatus?.({ step: 'listen', attempt, of: attempts })
    const wait = board.waitForLine(o.ready, waitMs)
    const nudge = setInterval(() => void board.send('').catch(() => {}), nudgeMs)
    let line: string | null
    try {
      line = await wait
    } finally {
      clearInterval(nudge)
    }
    if (line !== null) return { board, line }
  }
  return { board, line: null }
}

/** Runs the whole setup; resolves when the hornet is online, throws SetupError when a stage fails. */
export async function setupHornet(o: SetupOptions) {
  let board: HornetSerial | null = null
  try {
    if (o.build) {
      o.onStage('flash')
      await flashBoard(o.port, o.build, o)
    }
    o.onStage('restart')
    // The banner of the firmware; a board that booted before the port opened says nothing: it is nudged, reset and listened to again.
    // Tolerant at the end: a board that was up all the time still takes the settings.
    const up = await awaitConsole({
      open: () => reopenConsole(o.port, o.onLog),
      ready: (l) => lineMeaning(l) === 'booted',
      onStatus: o.onStatus, attempts: 3, waitMs: 8000,
    })
    board = up.board
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
