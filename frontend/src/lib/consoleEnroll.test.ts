import { afterEach, describe, expect, it, vi } from 'vitest'
import { needsInsecure, remoteCommands, setupRemote } from '@/lib/remoteSetup'
import { SetupError } from '@/lib/hornetSetup'
import type { SerialPortLike } from '@/lib/serial'

/** A panel on a serial port: says ready while listening, takes set lines, answers go with saved and a frame line. */
class FakePanel implements SerialPortLike {
  readable: ReadableStream<Uint8Array> | null = null
  writable: WritableStream<Uint8Array> | null = null
  received: string[] = []
  frameCode = 200
  silent = false
  private controller?: ReadableStreamDefaultController<Uint8Array>
  private buffer = ''

  async open() {
    this.readable = new ReadableStream({ start: (c) => { this.controller = c } })
    this.writable = new WritableStream({
      write: (chunk) => {
        this.buffer += new TextDecoder().decode(chunk)
        let nl: number
        while ((nl = this.buffer.indexOf('\n')) >= 0) {
          const line = this.buffer.slice(0, nl)
          this.buffer = this.buffer.slice(nl + 1)
          this.onLine(line)
        }
      },
    })
  }
  async close() {}
  async setSignals() {}

  private say(text: string) {
    setTimeout(() => { try { this.controller?.enqueue(new TextEncoder().encode(text + '\n')) } catch { /* closed */ } }, 2)
  }

  private onLine(line: string) {
    if (this.silent) return
    if (line === '') this.say('[monitor] ready')
    else if (line.startsWith('set ')) this.say('[monitor] ok')
    else if (line === 'go' || line === 'save') {
      this.received.push(line)
      this.say('[monitor] saved')
      if (line === 'go') this.say(`[monitor] frame ${this.frameCode}`)
    }
    if (line !== '') this.received.push(line)
  }
}

afterEach(() => vi.unstubAllGlobals())

function options(panel: FakePanel, extra: Partial<Parameters<typeof setupRemote>[0]['settings']> = {}, readNow = true) {
  vi.stubGlobal('navigator', { serial: { getPorts: async () => [] } })
  return {
    port: panel, settings: { ssid: 'Дача', pass: 'p w', url: 'https://hive.example', intervalMin: 10, ...extra }, readNow,
    issueToken: async () => 'abcd-efgh-jkmn-pqrs-tuvw', onStage: () => {}, onProgress: () => {}, onLog: () => {},
  }
}

describe('joining a monitor from the console page', () => {
  it('writes Wi-Fi, address, token and the certificate choice, then sends go itself', async () => {
    const panel = new FakePanel()
    await setupRemote(options(panel, { insecure: true }))
    expect(panel.received.slice(0, 7)).toEqual([
      'set ssid Дача', 'set pass p w', 'set url https://hive.example', 'set token abcd-efgh-jkmn-pqrs-tuvw', 'set interval 10', 'set insecure 1', 'go',
    ])
  }, 15_000)

  it('saves without reading when the panel is not at that Wi-Fi', async () => {
    const panel = new FakePanel()
    await setupRemote(options(panel, { insecure: false }, false))
    expect(panel.received).toContain('set insecure 0')
    expect(panel.received).toContain('save')
    expect(panel.received).not.toContain('go')
  }, 15_000)

  it('says what the hive answered when the panel read but got no picture', async () => {
    const panel = new FakePanel()
    panel.frameCode = 401
    const error = await setupRemote(options(panel)).catch((e) => e)
    expect(error).toBeInstanceOf(SetupError)
    expect(error).toMatchObject({ stage: 'read', reason: 'tokenRejected' })
  }, 15_000)

  it('leaves the old settings alone and the token unissued when the panel never answers', async () => {
    const panel = new FakePanel()
    panel.silent = true
    const issue = vi.fn(async () => 'never')
    const o = { ...options(panel), issueToken: issue, wait: { waitMs: 50, nudgeMs: 10 } }
    const error = await setupRemote({ ...o }).catch((e) => e)
    expect(error).toMatchObject({ reason: 'noAnswer' })
    expect(issue).not.toHaveBeenCalled()
  }, 15_000)
})

describe('the certificate of the address', () => {
  it('is not checked for an https address that is an IP number', () => {
    expect(needsInsecure('https://192.168.1.5:8443')).toBe(true)
    expect(needsInsecure('https://hive.x.keenetic.pro')).toBe(false)
    expect(needsInsecure('http://192.168.1.5:5080')).toBe(false)
    expect(needsInsecure('not an address')).toBe(false)
  })

  it('leaves the panel setting alone when the page does not say', () => {
    const s = { ssid: 'a', pass: 'b', url: 'https://h', token: 't', intervalMin: 10 }
    expect(remoteCommands(s, true).some((c) => c.startsWith('set insecure'))).toBe(false)
    expect(remoteCommands({ ...s, insecure: true }, false).slice(-2)).toEqual(['set insecure 1', 'save'])
  })
})
