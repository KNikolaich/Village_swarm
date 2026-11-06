// Setup of a remote monitor (the e-ink panel that reads its picture over HTTPS) over USB: flash it, wait for its console,
// send the Wi-Fi, the hive's address and the screen token, and either let it read the picture at once (the answer tells
// whether it all works) or only save (the panel is to stand at another Wi-Fi). The console is described in
// firmware/src/roles/remote_epd154.cpp.
import { awaitConsole, type ConsoleStatus, flashBoard, reopenConsole, SetupError, type SetupStage, sleep } from '@/lib/hornetSetup'
import type { HornetSerial, SerialPortLike } from '@/lib/serial'

export const remoteStages: SetupStage[] = ['port', 'flash', 'restart', 'settings', 'read']

export type RemoteSettings = {
  ssid: string
  pass: string
  url: string
  token: string
  intervalMin: number
  /** Do not check the hive's certificate (an address that is an IP, or a certificate the hive made itself). Left out: the panel keeps what it has. */
  insecure?: boolean
}

/** An https address of the hive by IP number cannot have a certificate for its name: the panel must not check it. */
export function needsInsecure(url: string): boolean {
  try {
    const u = new URL(url.trim())
    return u.protocol === 'https:' && /^\d{1,3}(\.\d{1,3}){3}$/.test(u.hostname)
  } catch {
    return false
  }
}

/** What the panel's console line says. */
export type PanelLine = 'ready' | 'saved' | 'wifiFailed' | { frame: number } | { error: string } | null

export function panelLine(line: string): PanelLine {
  const m = /^\[monitor\] (.*)$/.exec(line)
  if (!m) return null
  const text = m[1].trim()
  if (text === 'ready') return 'ready'
  if (text === 'saved') return 'saved'
  if (text === 'wifi failed') return 'wifiFailed'
  const frame = /^frame (-?\d+)$/.exec(text)
  if (frame) return { frame: Number(frame[1]) }
  if (text.startsWith('error:')) return { error: text.slice(6).trim() }
  return null
}

/** Console lines for the settings. A name or a password may hold spaces: the rest of the line is the value. */
export function remoteCommands(s: RemoteSettings, readNow: boolean): string[] {
  // "go" and "save" are sent here, never typed by the person: both write the settings to the panel's flash ("go" then reads at once).
  return [
    `set ssid ${s.ssid}`, `set pass ${s.pass}`, `set url ${s.url.trim()}`, `set token ${s.token.trim()}`,
    `set interval ${s.intervalMin}`, ...(s.insecure === undefined ? [] : [`set insecure ${s.insecure ? 1 : 0}`]), readNow ? 'go' : 'save',
  ]
}

/** Why a panel's answer is not a success: the key of remoteMonitor.why.*; null when the picture came (200) or is current (304). */
export function frameProblem(code: number): string | null {
  if (code === 200 || code === 304) return null
  if (code === 401 || code === 403) return 'tokenRejected'
  if (code === 404) return 'notDrawn'
  if (code === 429) return 'tooMany'
  return 'noHive'
}

export type RemoteSetupOptions = {
  port: SerialPortLike
  build?: { build: string; chipFamily: string }
  /** The settings but the token: that is issued only once the panel answers (issueToken). */
  settings: Omit<RemoteSettings, 'token'>
  /**
   * A new screen token from the hive. A new one replaces the panel's old one at once, so it is asked for only when the
   * panel is flashed and listening: a flash or a restart that fails leaves the old token working.
   */
  issueToken: () => Promise<string>
  /** Read the picture now (the panel is at this Wi-Fi) or only save the settings. */
  readNow: boolean
  onStage: (stage: SetupStage) => void
  onProgress: (percent: number) => void
  onLog: (line: string) => void
  /** What the page does while it waits for the panel (listening, resetting it, reopening the port). */
  onStatus?: (status: ConsoleStatus) => void
  /** How long each try listens and how often it nudges (tests make them short). */
  wait?: { waitMs: number; nudgeMs: number }
}

/** Resolves when the panel took the settings (and, with readNow, read its picture); throws SetupError otherwise. */
export async function setupRemote(o: RemoteSetupOptions) {
  let board: HornetSerial | null = null
  try {
    if (o.build) {
      o.onStage('flash')
      await flashBoard(o.port, o.build, o)
    }
    o.onStage('restart')
    // After a power-on or a reset the panel says "ready" for twelve seconds and waits for the console (a line sent keeps it
    // open a minute). The board also resets when the port is opened, so that window is often missed: awaitConsole nudges the
    // panel, resets it through DTR/RTS, reopens the port and listens again before it gives up.
    const up = await awaitConsole({
      open: () => reopenConsole(o.port, o.onLog),
      ready: (l) => panelLine(l) === 'ready',
      onStatus: o.onStatus,
      ...o.wait,
    })
    board = up.board
    if (up.line === null) throw new SetupError('restart', 'noAnswer')

    o.onStage('settings')
    const token = await o.issueToken()
    const answer = board.waitForLine((l) => {
      const p = panelLine(l)
      // "saved" comes first, before the panel reads: with readNow only what the read said ends the wait
      return (!o.readNow && p === 'saved') || p === 'wifiFailed' || (typeof p === 'object' && p !== null)
    }, o.readNow ? 90_000 : 15_000)
    for (const command of remoteCommands({ ...o.settings, token }, o.readNow)) {
      await board.send(command)
      await sleep(250)
    }
    o.onStage('read')
    const line = await answer
    if (line === null) throw new SetupError('read', 'noAnswer')
    const p = panelLine(line)
    if (p === 'wifiFailed') throw new SetupError('read', 'wifiFailed')
    if (typeof p === 'object' && p !== null && 'error' in p) throw new SetupError('settings', 'other', p.error)
    if (typeof p === 'object' && p !== null && 'frame' in p) {
      const problem = frameProblem(p.frame)
      if (problem) throw new SetupError('read', problem, `HTTP ${p.frame}`)
    }
  } finally {
    await board?.close()
  }
}
