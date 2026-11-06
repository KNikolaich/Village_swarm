import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { SerialPortLike } from '@/lib/serial'

// The panel's console, played back: it says "ready" (or nothing) and then "saved" once it got the settings.
const panel = vi.hoisted(() => ({ answers: true, sent: [] as string[], log: [] as string[] }))

vi.mock('@/lib/hornetSetup', async (actual) => ({
  ...(await actual<typeof import('@/lib/hornetSetup')>()),
  flashBoard: vi.fn(async () => { panel.log.push('flashed') }),
  sleep: vi.fn(async () => {}),
  reopenConsole: vi.fn(async () => ({
    waitForLine: async (match: (line: string) => boolean) => {
      for (const line of ['[monitor] ready', '[monitor] saved'])
        if (panel.answers && match(line)) return line
      return null
    },
    send: async (line: string) => { panel.sent.push(line) },
    close: async () => {},
  })),
}))

const { setupRemote } = await import('@/lib/remoteSetup')

function options(issueToken: () => Promise<string>) {
  return {
    port: {} as SerialPortLike, build: { build: 'monitor-crowpanel37', chipFamily: 'ESP32-S3' },
    settings: { ssid: 'Дача', pass: 'p', url: 'https://hive', intervalMin: 30 }, readNow: false, issueToken,
    onStage: () => {}, onProgress: () => {}, onLog: () => {},
  }
}

describe('a new screen token for a reflashed panel', () => {
  beforeEach(() => { panel.answers = true; panel.sent = []; panel.log = [] })

  it('is issued only after the flashed panel answers, and sent to it', async () => {
    const issue = vi.fn(async () => { panel.log.push('issued'); return 'aaaa-bbbb' })
    await setupRemote(options(issue))
    expect(panel.log).toEqual(['flashed', 'issued'])
    expect(panel.sent).toContain('set token aaaa-bbbb')
  })

  it('is not issued when the panel does not answer, so its old token keeps working', async () => {
    panel.answers = false
    const issue = vi.fn(async () => 'never')
    await expect(setupRemote(options(issue))).rejects.toThrow()
    expect(issue).not.toHaveBeenCalled()
  })
})
