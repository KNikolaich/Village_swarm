import { describe, expect, it } from 'vitest'
import type { LatestValueDto } from '@/api/client'
import { hiveName, hiveTiles } from './hiveStats'

const v = (deviceId: string, metric: string, value: number): LatestValueDto => ({ deviceId, metric, value, ts: '2026-11-08T10:00:00Z' })

describe('hiveTiles', () => {
  it('pairs memory and disk with their totals and keeps the CPU as it is', () => {
    const tiles = hiveTiles('hive-city', [
      v('hive-city', 'cpu_temperature', 48.5),
      v('hive-city', 'cpu_load_pct', 37.5),
      v('hive-city', 'mem_used_mb', 700),
      v('hive-city', 'mem_total_mb', 1886),
      v('hive-city', 'disk_free_gb', 20.5),
      v('hive-city', 'disk_total_gb', 30),
      v('hive-home', 'cpu_temperature', 90),
    ])
    expect(tiles.map((x) => x.key)).toEqual(['cpu_temperature', 'cpu_load_pct', 'mem', 'disk'])
    expect(tiles[2].value).toContain('700')
    expect(tiles[2].value).toContain('1')
    expect(tiles[3].value).toContain('20')
  })

  it('falls back to the percentage and leaves out what the board did not report', () => {
    const tiles = hiveTiles('hive-home', [v('hive-home', 'disk_used_pct', 41), v('hive-home', 'mem_used_pct', 60)])
    expect(tiles.map((x) => x.key)).toEqual(['mem', 'disk_used_pct'])
    expect(hiveTiles('hive-home', [])).toEqual([])
  })
})

describe('hiveName', () => {
  const hive = (deviceId: string) => ({ deviceId, type: 'hive', name: 'Улей' })
  const cluster = { node: 'home', alias: 'Дача', otherAlias: 'Город' }

  it('uses the alias of the hive each device stands for', () => {
    expect(hiveName(hive('hive-home'), cluster)).toBe('Дача')
    expect(hiveName(hive('hive-city'), cluster)).toBe('Город')
    expect(hiveName(hive('hive-home'), { node: 'city', alias: 'Sbrbn', otherAlias: 'Home' })).toBe('Home')
  })

  it('keeps the name when there is no alias, or the device is not a hive', () => {
    expect(hiveName(hive('hive-home'), { node: 'home', alias: null })).toBe('Улей')
    expect(hiveName({ deviceId: 'hive-home', type: 'meteo', name: 'Метео' }, cluster)).toBe('Метео')
    expect(hiveName(hive('hive-home'), undefined)).toBe('Улей')
  })
})
