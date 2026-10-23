import { describe, expect, it } from 'vitest'
import type { DeviceDto, LatestValueDto } from '@/api/client'
import { dashboardItems, shownKeys, shownReadings, toggled, zoneTiles } from './dashboard'

const device = (deviceId: string, type: string, extra: Partial<DeviceDto> = {}) =>
  ({ deviceId, type, name: deviceId, status: 'online', zoneId: 1, sort: 0, features: null, ...extra }) as DeviceDto
const value = (deviceId: string, metric: string, v = 20): LatestValueDto => ({ deviceId, metric, ts: '2026-10-23T10:00:00Z', value: v })
const info = (...commands: string[]) => ({ commands }) as unknown as DeviceDto['info']

describe('dashboard choice', () => {
  const bridge = device('ble-bridge', 'ble')
  const latest = [value('ble-bridge', 'temperature'), value('ble-bridge', 'temperature_2'), value('ble-bridge', 'temperature_3')]

  it('shows every probe until someone ticks, then only the ticked ones', () => {
    expect([...shownKeys(bridge, latest)]).toEqual(['temperature', 'temperature_2', 'temperature_3'])
    const chosen = { ...bridge, dashboard: toggled(bridge, latest, 'temperature', false) }
    expect(chosen.dashboard).toEqual(['temperature_2', 'temperature_3'])
    expect(shownReadings(chosen, latest).map((v) => v.metric)).toEqual(['temperature_2', 'temperature_3'])
  })

  it('keeps the voltage of a heater off by default and offers its relay as a banner', () => {
    const heater = device('heat-1', 'heat', { features: ['thermometer', 'relay', 'heater'], info: info('relay') })
    const own = [value('heat-1', 'temperature'), value('heat-1', 'battery_voltage', 5)]
    expect(dashboardItems(heater, own)).toEqual([
      { key: 'temperature', kind: 'metric', byDefault: true },
      { key: 'battery_voltage', kind: 'metric', byDefault: false },
      { key: 'relay', kind: 'button', byDefault: true },
    ])
  })

  it('gives a camera a snapshot and a light button instead of a relay', () => {
    const cam = device('cam-hall', 'guard-cam', { info: info('snapshot', 'relay') })
    expect(dashboardItems(cam, []).map((i) => i.key)).toEqual(['snapshot', 'light'])
    expect(shownKeys({ ...cam, dashboard: ['snapshot'] }, []).has('light')).toBe(false)
  })

  it('never puts the hive diagnostics on a zone', () => {
    const hive = device('hive-pi', 'hive')
    expect(dashboardItems(hive, [value('hive-pi', 'cpu_temperature', 50)])).toEqual([])
  })

  it('builds tiles: probes by name, humidity under its temperature, a heater with its range, a battery alone', () => {
    const chosen = { ...bridge, name: 'Мост', dashboard: ['temperature_2', 'temperature_3'] }
    const meteo = device('meteo-1', 'meteo', { name: 'Под лестницей' })
    const heater = device('heat-1', 'heat', { name: 'Обогрев', features: ['thermometer', 'relay', 'heater'], heater: { minC: 18, maxC: 20, maxOnS: 3600 } })
    const battery = device('relay-bat', 'relay', { name: 'Батарея 12V', features: ['relay'] })
    const all = [...latest, value('meteo-1', 'temperature'), value('meteo-1', 'humidity', 36), value('heat-1', 'temperature', 19),
      value('heat-1', 'battery_voltage', 5), value('relay-bat', 'battery_voltage', 12.6)]
    const tiles = zoneTiles([chosen, meteo, heater, battery], all)
    expect(tiles.map((x) => [x.title, x.main.metric])).toEqual([
      ['Мост · t2', 'temperature_2'], ['Мост · t3', 'temperature_3'],
      ['Под лестницей', 'temperature'], ['Обогрев', 'temperature'], ['Батарея 12V', 'battery_voltage'],
    ])
    expect(tiles[2].note).toContain('36')
    expect(tiles[3].range).toMatch(/18.*20/)
  })
})
