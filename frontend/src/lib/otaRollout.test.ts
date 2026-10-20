import { describe, expect, it } from 'vitest'
import type { DeviceDto, Schemas } from '@/api/client'
import { candidates, newerBuild, preselected, toSend } from './otaRollout'

const now = Date.parse('2026-10-20T18:00:00Z')
const device = (deviceId: string, extra: Partial<DeviceDto> = {}) =>
  ({ deviceId, name: deviceId, type: 'meteo', hw: 'esp32c3-supermini', fw: '0.1.0', driver: 'hornet', status: 'online', lastSeenAt: '2026-10-20T17:59:50Z', ...extra }) as DeviceDto
const build = (name: string, extra: Partial<Schemas['FirmwareBuildDto']> = {}) =>
  ({ build: name, role: 'meteo', board: 'esp32c3-supermini', chipFamily: 'ESP32-C3', built: '2026-10-19T10:00:00Z', version: '0.3.0', signed: false, ...extra }) as Schemas['FirmwareBuildDto']

describe('newerBuild', () => {
  it('takes the newest build for the role and board', () => {
    const old = build('meteo-old', { version: '0.2.0', built: '2026-10-01T10:00:00Z' })
    const fresh = build('meteo-c3')
    const otherBoard = build('meteo-esp01', { board: 'esp-01', built: '2026-10-20T10:00:00Z' })
    expect(newerBuild(device('a'), [old, otherBoard, fresh])).toBe(fresh)
  })

  it('offers nothing to a hornet that runs it already', () => {
    expect(newerBuild(device('a', { fw: '0.3.0' }), [build('meteo-c3')])).toBeNull()
  })
})

describe('candidates', () => {
  const c3 = build('meteo-c3')
  const esp01 = build('meteo-esp01', { board: 'esp-01', chipFamily: 'ESP8266' })
  const devices = [
    device('Баня', { hw: 'esp-01' }),
    device('Ворота'),
    device('Гараж', { status: 'offline' }),
    device('Аккумулятор', { sleepS: 600, sleepMode: 'deep', lastSeenAt: '2026-10-20T17:50:00Z' }),
    device('Новый', { fw: '0.3.0' }),
    device('Розетка', { driver: 'tasmota' }),
  ]
  const list = candidates(devices, [c3, esp01], now)

  it('lists the outdated hornets, ESP8266 last, and says who cannot take it now', () => {
    expect(list.map((c) => [c.device.deviceId, c.blocker, c.noRollback])).toEqual([
      ['Аккумулятор', 'asleep', false],
      ['Ворота', '', false],
      ['Гараж', 'offline', false],
      ['Баня', '', true],
    ])
  })

  it('ticks only the hornets that can take it and can roll back', () => {
    expect(preselected(list)).toEqual(['Ворота'])
  })

  it('sends ESP8266 boards only with the confirmation, and never a blocked hornet', () => {
    const chosen = ['Ворота', 'Баня', 'Гараж', 'Аккумулятор']
    expect(toSend(list, chosen, false)).toEqual(['Ворота'])
    expect(toSend(list, chosen, true)).toEqual(['Ворота', 'Баня'])
  })
})
