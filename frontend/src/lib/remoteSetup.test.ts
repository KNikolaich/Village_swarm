import { describe, expect, it } from 'vitest'
import { frameProblem, panelLine, remoteCommands } from '@/lib/remoteSetup'
import { portalLink } from '@/lib/setupLink'

describe('remote monitor setup over USB', () => {
  it('reads what the panel says', () => {
    expect(panelLine('[monitor] ready')).toBe('ready')
    expect(panelLine('[monitor] saved')).toBe('saved')
    expect(panelLine('[monitor] wifi failed')).toBe('wifiFailed')
    expect(panelLine('[monitor] frame 200')).toEqual({ frame: 200 })
    expect(panelLine('[monitor] frame -1')).toEqual({ frame: -1 })
    expect(panelLine('[monitor] error: need ssid')).toEqual({ error: 'need ssid' })
    expect(panelLine('[monitor] ok')).toBeNull()
    expect(panelLine('ets Jul 29 2019')).toBeNull()
  })

  it('sends the settings, the whole line being the value, and ends with go or save', () => {
    const s = { ssid: 'Дом 2.4', pass: 'p ass', url: ' https://h.example/ ', token: 'abcd-efgh-jkmn-pqrs-tuvw', intervalMin: 15 }
    expect(remoteCommands(s, true)).toEqual([
      'set ssid Дом 2.4', 'set pass p ass', 'set url https://h.example/', 'set token abcd-efgh-jkmn-pqrs-tuvw', 'set interval 15', 'go',
    ])
    expect(remoteCommands(s, false).at(-1)).toBe('save')
  })

  it('tells what an answer of the hive means', () => {
    expect(frameProblem(200)).toBeNull()
    expect(frameProblem(304)).toBeNull()
    expect(frameProblem(401)).toBe('tokenRejected')
    expect(frameProblem(404)).toBe('notDrawn')
    expect(frameProblem(429)).toBe('tooMany')
    expect(frameProblem(-1)).toBe('noHive')
    expect(frameProblem(502)).toBe('noHive')
  })

  it('builds the address the portal form is filled in from', () => {
    const link = new URL(portalLink({ url: 'https://h.example', token: 'abcd-efgh', ssid: 'Дом', pass: 'a&b=c', intervalMin: 10 }))
    expect(link.origin + link.pathname).toBe('http://192.168.4.1/')
    expect(link.searchParams.get('pass')).toBe('a&b=c')
    expect(link.searchParams.get('ssid')).toBe('Дом')
    expect(link.searchParams.get('token')).toBe('abcd-efgh')
    expect(portalLink({ url: 'https://h', token: 't' })).not.toContain('ssid')
  })
})
