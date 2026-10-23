import type { DeviceDto, LatestValueDto } from '@/api/client'
import { metricLabel } from '@/i18n'
import { hasRelay } from '@/lib/features'
import { formatValue } from '@/lib/format'
import { announcedCommands, mainReadings } from '@/lib/swarm'

/**
 * What a zone card and the overview show of a hornet (DeviceDto.dashboard, ticked in its card): readings by metric
 * name and buttons. Until someone ticks anything the defaults of its kind apply.
 */
/** Kinds of climate zone (backend ZoneKind). */
export const zoneKinds = ['garden', 'house', 'perimeter', 'tech'] as const

export const buttons = ['snapshot', 'light', 'relay'] as const
export type DashboardButton = (typeof buttons)[number]

export interface DashboardItem {
  key: string
  kind: 'metric' | 'button'
  /** Shown when nobody chose yet. */
  byDefault: boolean
}

/** The hive's own diagnostics: they stay on the hornet's page, never on a zone. */
const diagnostics = new Set(['cpu_temperature', 'disk_used_pct'])

const isCamera = (d: DeviceDto) => announcedCommands(d).includes('snapshot')

/** A heater's supply voltage says little: the reading people look at is the temperature it keeps. */
function metricByDefault(d: DeviceDto, metric: string): boolean {
  if (metric === 'battery_voltage') return d.type !== 'heat' && !d.features?.includes('heater')
  return true
}

/**
 * Everything the hornet could show, in the order of its readings: what it reports now (what it is used for, see
 * showsMetric), anything it was ticked for before and has not reported since, then its buttons.
 */
export function dashboardItems(d: DeviceDto, latest: readonly LatestValueDto[]): DashboardItem[] {
  const metrics = mainReadings(d, latest).map((v) => v.metric).filter((m) => !diagnostics.has(m))
  for (const key of d.dashboard ?? [])
    if (!metrics.includes(key) && !(buttons as readonly string[]).includes(key)) metrics.push(key)
  const items: DashboardItem[] = metrics.map((key) => ({ key, kind: 'metric', byDefault: metricByDefault(d, key) }))

  const commands = announcedCommands(d)
  if (isCamera(d)) {
    items.push({ key: 'snapshot', kind: 'button', byDefault: true })
    // The camera's output (white LED or a floodlight relay) is its light.
    if (commands.includes('relay')) items.push({ key: 'light', kind: 'button', byDefault: true })
  } else if (commands.includes('relay') && hasRelay(d.features)) {
    items.push({ key: 'relay', kind: 'button', byDefault: true })
  }
  return items
}

/** The keys shown: the ones ticked, or the defaults while nothing was chosen. */
export function shownKeys(d: DeviceDto, latest: readonly LatestValueDto[]): Set<string> {
  if (d.dashboard) return new Set(d.dashboard)
  return new Set(dashboardItems(d, latest).filter((i) => i.byDefault).map((i) => i.key))
}

/** The readings of a hornet that its zone shows, newest value of each (in the order of its readings). */
export function shownReadings(d: DeviceDto, latest: readonly LatestValueDto[]): LatestValueDto[] {
  const shown = shownKeys(d, latest)
  return mainReadings(d, latest).filter((v) => shown.has(v.metric) && !diagnostics.has(v.metric))
}

/** The list saved when one item is ticked or unticked; starts from the defaults the first time. */
export function toggled(d: DeviceDto, latest: readonly LatestValueDto[], key: string, on: boolean): string[] {
  const shown = shownKeys(d, latest)
  if (on) shown.add(key)
  else shown.delete(key)
  const order = dashboardItems(d, latest).map((i) => i.key)
  return [...shown].sort((a, b) => order.indexOf(a) - order.indexOf(b))
}

/** "t2" for the second probe of a hornet, "t1" for its first. */
export const probeName = (metric: string) => `t${/^temperature_(\d+)$/.exec(metric)?.[1] ?? '1'}`

/** Whether the hornet reports several temperatures (a bridge with probes), so its tiles and lines say which. */
const probes = (d: DeviceDto, latest: readonly LatestValueDto[]) =>
  latest.filter((v) => v.deviceId === d.deviceId && v.metric.startsWith('temperature')).length > 1

/** The name of one reading of a hornet on a zone: the hornet's, with the probe when it has several. */
export function readingTitle(d: DeviceDto, metric: string, latest: readonly LatestValueDto[]): string {
  return metric.startsWith('temperature') && probes(d, latest) ? `${d.name} · ${probeName(metric)}` : d.name
}

export interface Tile {
  key: string
  device: DeviceDto
  title: string
  main: LatestValueDto
  /** Second line, before the time. */
  note?: string
  /** A heater's range, under the value: smaller and dimmer than it. */
  range?: string
}

/**
 * The tiles of a zone, from what each hornet is ticked to show (DeviceDto.dashboard): a temperature with the humidity
 * of the same hornet under it, a heater's temperature with the range it keeps, a battery as its voltage (no chart).
 */
export function zoneTiles(devices: readonly DeviceDto[], latest: readonly LatestValueDto[]): Tile[] {
  const tiles: Tile[] = []
  for (const d of devices) {
    const shown = shownReadings(d, latest)
    const temps = shown.filter((v) => v.metric.startsWith('temperature'))
    const humidity = shown.find((v) => v.metric === 'humidity')
    const heater = d.heater
    temps.forEach((v, i) => tiles.push({
      key: `${d.deviceId}/${v.metric}`,
      device: d,
      title: readingTitle(d, v.metric, latest),
      main: v,
      note: i === 0 && humidity ? `${metricLabel('humidity')} ${formatValue('humidity', humidity.value)}` : undefined,
      range: i === 0 && heater ? `${formatValue('temperature', heater.minC)} – ${formatValue('temperature', heater.maxC)}` : undefined,
    }))
    for (const v of shown) {
      if (v.metric.startsWith('temperature') || (v.metric === 'humidity' && temps.length > 0)) continue
      tiles.push({ key: `${d.deviceId}/${v.metric}`, device: d, title: d.name, main: v, note: metricLabel(v.metric) })
    }
  }
  return tiles
}
