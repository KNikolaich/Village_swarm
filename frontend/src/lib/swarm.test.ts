import { describe, expect, it } from 'vitest'
import type { DeviceDto, LatestValueDto, ZoneDto } from '@/api/client'
import { groupDevices, mainReadings, quickControls } from './swarm'

const device = (deviceId: string, type: string, extra: Partial<DeviceDto> = {}) =>
  ({ deviceId, type, name: deviceId, status: 'online', rssi: -50, zoneId: null, sort: 0, ...extra }) as DeviceDto
const zone = (id: number, name: string, sort = 0) => ({ id, name, kind: 'house', deviceCount: 0, sort }) as ZoneDto
const labels = { hives: 'Ульи', noZone: 'Без зоны', type: (t: string) => t }
const ids = (groups: ReturnType<typeof groupDevices>) => groups.map((g) => [g.title, g.devices.map((d) => d.deviceId)])

describe('groupDevices', () => {
  const devices = [
    device('relay-b', 'relay', { zoneId: 2 }),
    device('meteo-a', 'meteo', { zoneId: 1 }),
    device('hive-pi', 'hive'),
    device('heat-c', 'heat', { zoneId: 2 }),
    device('meteo-z', 'meteo'),
  ]
  // The API lists zones by their sort: zone 2 was moved first.
  const zones = [zone(2, 'Гостиная', 0), zone(1, 'Улица', 5)]

  it('put the hives first and the zones in the order given, hornets without a zone last', () => {
    expect(ids(groupDevices(devices, zones, 'zone', labels))).toEqual([
      ['Ульи', ['hive-pi']],
      ['Гостиная', ['heat-c', 'relay-b']],
      ['Улица', ['meteo-a']],
      ['Без зоны', ['meteo-z']],
    ])
  })

  it('list kinds in their usual order when grouped by kind', () => {
    expect(ids(groupDevices(devices, zones, 'type', labels))).toEqual([
      ['Ульи', ['hive-pi']],
      ['meteo', ['meteo-a', 'meteo-z']],
      ['heat', ['heat-c']],
      ['relay', ['relay-b']],
    ])
  })

  it("follow each hornet's sort, then trouble first", () => {
    const list = [device('a', 'meteo', { sort: 2 }), device('b', 'meteo', { sort: 1 }), device('c', 'meteo', { status: 'offline' }), device('d', 'meteo')]
    expect(ids(groupDevices(list, [], 'all', labels))).toEqual([[null, ['c', 'd', 'b', 'a']]])
  })
})

describe('mainReadings', () => {
  const v = (deviceId: string, metric: string): LatestValueDto => ({ deviceId, metric, ts: '2026-10-17T09:00:00Z', value: 1 })
  it('show what the hornet is used for, temperature first', () => {
    const d = device('heat-1', 'heat', { features: ['relay', 'heater'] })
    const latest = [v('heat-1', 'battery_voltage'), v('heat-1', 'temperature'), v('other', 'humidity')]
    expect(mainReadings(d, latest).map((r) => r.metric)).toEqual(['battery_voltage'])
    const meteo = device('meteo-1', 'meteo')
    expect(mainReadings(meteo, [v('meteo-1', 'battery_voltage'), v('meteo-1', 'humidity'), v('meteo-1', 'temperature')]).map((r) => r.metric))
      .toEqual(['temperature', 'humidity', 'battery_voltage'])
  })
})

describe('quickControls', () => {
  it('offer a relay toggle with its state and a snapshot when announced', () => {
    const relay = device('relay-1', 'relay', { info: { commands: ['relay', 'reboot'] }, state: { relay: 'on' }, features: ['relay'] })
    expect(quickControls(relay)).toEqual({ relays: [{ channel: 'relay', on: true }], snapshot: false })
    const cam = device('cam-1', 'guard-cam', { info: { commands: ['snapshot'] }, state: null })
    expect(quickControls(cam)).toEqual({ relays: [], snapshot: true })
  })

  it('offer no relay for a hornet not used as one', () => {
    const meteo = device('heat-2', 'heat', { info: { commands: ['relay'] }, features: ['thermometer'] })
    expect(quickControls(meteo).relays).toEqual([])
  })
})
