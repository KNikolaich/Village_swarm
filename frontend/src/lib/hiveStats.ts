import type { DeviceDto, LatestValueDto } from '@/api/client'
import { t } from '@/i18n'
import { formatValue } from '@/lib/format'

/** Readings that only make sense together with another one (a total next to what is used): the swarm row shows percentages, the overview shows pairs. */
export const hiveDetailMetrics = new Set(['mem_total_mb', 'mem_used_mb', 'disk_total_gb', 'disk_free_gb'])

/** What the hive reports about its board: shown on the overview and kept out of the zones. */
export const hiveMetrics = new Set(['temperature', 'cpu_temperature', 'cpu_load_pct', 'mem_used_pct', 'disk_used_pct', ...hiveDetailMetrics])

export interface HiveTile {
  /** i18n key under metrics.* */
  key: string
  value: string
  /** When it was read. */
  ts: string
}

function number(n: number, digits = 0): string {
  return n.toLocaleString(undefined, { maximumFractionDigits: digits })
}

/**
 * The tiles of one hive: the CPU temperature and load as they are, memory and disk as "used / total" and "free / total"
 * (falling back to the percentage when the totals are not known), whatever the board did not report is left out.
 */
export function hiveTiles(deviceId: string, latest: readonly LatestValueDto[]): HiveTile[] {
  const own = new Map(latest.filter((v) => v.deviceId === deviceId).map((v) => [v.metric, v]))
  const tiles: HiveTile[] = []
  const temperature = own.get('cpu_temperature')
  if (temperature) tiles.push({ key: 'cpu_temperature', value: formatValue('cpu_temperature', temperature.value), ts: temperature.ts })
  const load = own.get('cpu_load_pct')
  if (load) tiles.push({ key: 'cpu_load_pct', value: formatValue('cpu_load_pct', load.value), ts: load.ts })

  const memUsed = own.get('mem_used_mb')
  const memTotal = own.get('mem_total_mb')
  const memPct = own.get('mem_used_pct')
  if (memUsed && memTotal)
    tiles.push({ key: 'mem', value: `${number(memUsed.value)} / ${number(memTotal.value)} ${t('units.mb')}`, ts: memUsed.ts })
  else if (memPct) tiles.push({ key: 'mem', value: formatValue('mem_used_pct', memPct.value), ts: memPct.ts })

  const free = own.get('disk_free_gb')
  const total = own.get('disk_total_gb')
  const diskPct = own.get('disk_used_pct')
  if (free && total)
    tiles.push({ key: 'disk', value: `${number(free.value, 1)} / ${number(total.value, 1)} ${t('units.gb')}`, ts: free.ts })
  else if (diskPct) tiles.push({ key: 'disk_used_pct', value: formatValue('disk_used_pct', diskPct.value), ts: diskPct.ts })
  return tiles
}

/** The name of a hive device: the node's alias when it has one ("Дача"), the device's own name otherwise. */
export function hiveName(
  device: Pick<DeviceDto, 'deviceId' | 'type' | 'name'>,
  cluster: { node: string; alias?: string | null; otherAlias?: string | null } | undefined,
): string {
  if (device.type !== 'hive' || !cluster) return device.name
  const own = `hive-${cluster.node}`
  const other = cluster.node === 'city' ? 'hive-home' : 'hive-city'
  if (device.deviceId === own && cluster.alias) return cluster.alias
  if (device.deviceId === other && cluster.otherAlias) return cluster.otherAlias
  return device.name
}
