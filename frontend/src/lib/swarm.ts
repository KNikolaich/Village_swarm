import type { DeviceDto, LatestValueDto, ZoneDto } from '@/api/client'
import { hasRelay, showsMetric } from '@/lib/features'

/** How the swarm list is grouped: by zone, by kind of hornet, or one list. */
export const groupings = ['zone', 'type', 'all'] as const
export type Grouping = (typeof groupings)[number]

/** One block of the swarm list. title null: no header (the whole swarm in one list). */
export interface SwarmGroup {
  key: string
  title: string | null
  devices: DeviceDto[]
}

/** "Problems first": offline, then weak signal, then the rest; removed ones last (spec 8.2 screen 5). */
export function badness(d: DeviceDto): number {
  if (d.status === 'disabled') return 4
  if (d.status !== 'online') return 0
  if ((d.rssi ?? 0) < -80) return 1
  return 2
}

/** The hornet's own place first; with equal places (all 0 until someone sets them) the usual order. */
export function compareDevices(a: DeviceDto, b: DeviceDto): number {
  return (a.sort ?? 0) - (b.sort ?? 0) || badness(a) - badness(b) || a.deviceId.localeCompare(b.deviceId)
}

/** Kinds in the order they are listed when grouped by kind; kinds not named here follow by code. */
const typeOrder = ['meteo', 'heat', 'relay', 'monitor', 'guard-cam', 'nvr', 'ipcam', 'leak', 'plant']

function typeRank(type: string): number {
  const i = typeOrder.indexOf(type)
  return i < 0 ? typeOrder.length : i
}

/**
 * The swarm list in groups. Hives always come first, in a group of their own; zones keep the order the API gives
 * (their sort, then id), hornets without a zone go last.
 */
export function groupDevices(
  devices: readonly DeviceDto[],
  zones: readonly ZoneDto[],
  grouping: Grouping,
  labels: { hives: string; noZone: string; type: (type: string) => string },
): SwarmGroup[] {
  const sorted = [...devices].sort(compareDevices)
  const hives = sorted.filter((d) => d.type === 'hive')
  const hornets = sorted.filter((d) => d.type !== 'hive')
  const groups: SwarmGroup[] = hives.length > 0 ? [{ key: 'hives', title: labels.hives, devices: hives }] : []

  if (grouping === 'all') {
    if (hornets.length > 0) groups.push({ key: 'all', title: null, devices: hornets })
    return groups
  }

  if (grouping === 'zone') {
    for (const zone of zones) {
      const inZone = hornets.filter((d) => d.zoneId === zone.id)
      if (inZone.length > 0) groups.push({ key: `zone-${zone.id}`, title: zone.name, devices: inZone })
    }
    const known = new Set(zones.map((z) => z.id))
    const loose = hornets.filter((d) => d.zoneId == null || !known.has(d.zoneId))
    if (loose.length > 0) groups.push({ key: 'zone-none', title: labels.noZone, devices: loose })
    return groups
  }

  const types = [...new Set(hornets.map((d) => d.type))].sort((a, b) => typeRank(a) - typeRank(b) || a.localeCompare(b))
  for (const type of types) groups.push({ key: `type-${type}`, title: labels.type(type), devices: hornets.filter((d) => d.type === type) })
  return groups
}

/**
 * Devices that belong to another one in the list (an NVR's cameras) go under it instead of into the groups. A child whose
 * parent is not in the list (removed, or hidden with the deceased) stays on its own.
 */
export function splitChildren(devices: readonly DeviceDto[]): { top: DeviceDto[]; children: Map<string, DeviceDto[]> } {
  const present = new Set(devices.map((d) => d.deviceId))
  const children = new Map<string, DeviceDto[]>()
  const top: DeviceDto[] = []
  for (const d of devices) {
    if (d.parent && d.parent !== d.deviceId && present.has(d.parent)) children.set(d.parent, [...(children.get(d.parent) ?? []), d])
    else top.push(d)
  }
  for (const list of children.values()) list.sort(compareDevices)
  return { top, children }
}

/** Readings shown in a swarm row, in this order; anything else the hornet reports follows by name. */
const readingOrder = ['temperature', 'temperature_2', 'humidity', 'pressure', 'battery_voltage', 'cpu_temperature', 'disk_used_pct']

/** The hornet's main readings (what it is used for, no diagnostics: signal and memory stay on its own page). */
export function mainReadings(d: DeviceDto, latest: readonly LatestValueDto[]): LatestValueDto[] {
  const rank = (m: string) => {
    const i = readingOrder.indexOf(m)
    return i < 0 ? readingOrder.length : i
  }
  return latest
    .filter((v) => v.deviceId === d.deviceId && showsMetric(d.features, v.metric, d.thermometer))
    .sort((a, b) => rank(a.metric) - rank(b.metric) || a.metric.localeCompare(b.metric))
}

/** Command names the hornet announced in its info message (contracts/schemas/info.schema.json "commands"). */
export function announcedCommands(d: DeviceDto): string[] {
  const info = d.info as { commands?: unknown } | null
  return Array.isArray(info?.commands) ? info.commands.filter((c): c is string => typeof c === 'string') : []
}

/** Relay channel names come from the hornet's retained state ({channel: on|off}); "relay" ("heater" for a heater) is the firmware default. */
export function relayChannels(d: DeviceDto): string[] {
  const keys = Object.keys((d.state as Record<string, unknown> | null) ?? {}).filter((k) => k !== 'ir_last')
  return keys.length > 0 ? keys : [d.features?.includes('heater') ? 'heater' : 'relay']
}

/** What a swarm row offers to press: relay channels (a relay the hornet is not used as gets none) and a snapshot. */
export function quickControls(d: DeviceDto): { relays: { channel: string; on: boolean | null }[]; snapshot: boolean } {
  const commands = announcedCommands(d)
  const state = (d.state as Record<string, unknown> | null) ?? {}
  const relays = commands.includes('relay') && hasRelay(d.features)
    ? relayChannels(d).map((channel) => ({ channel, on: state[channel] === 'on' ? true : state[channel] === 'off' ? false : null }))
    : []
  return { relays, snapshot: commands.includes('snapshot') }
}

/** Capabilities a hornet announced in its info message (contracts/schemas/info.schema.json "caps"). */
export const hasCap = (d: DeviceDto, cap: string) => {
  const caps = (d.info as { caps?: unknown } | null)?.caps
  return Array.isArray(caps) && caps.includes(cap)
}
