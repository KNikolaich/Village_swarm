import { ChevronRight, Wifi, WifiOff } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router'
import type { DeviceDto, LatestValueDto, ZoneDto } from '@/api/client'
import { useDevices, useEvents, useLatestTelemetry, useMe, useZones } from '@/api/queries'
import { EventCard, type PhotoRef } from '@/components/EventCard'
import { GuardCard } from '@/components/GuardCard'
import { Lightbox } from '@/components/Lightbox'
import { WeatherCard } from '@/components/WeatherCard'
import { ZoneTiles } from '@/components/ZoneTiles'
import { bannerKind, zoneTiles } from '@/lib/dashboard'
import { ZoneBanners } from '@/components/DeviceBanners'
import { Badge, Card, CardContent, CardHeader, CardTitle, Empty, Skeleton } from '@/components/ui/primitives'
import { metricLabel, statusLabel, t } from '@/i18n'
import { formatAgo, formatValue } from '@/lib/format'
import { cn } from '@/lib/utils'

/** Screen 1 (spec 8.2): swarm health, temperatures, latest events with photo previews. */
export function OverviewPage() {
  const devices = useDevices()
  const latest = useLatestTelemetry()
  const events = useEvents({ limit: 5 })
  const zones = useZones()
  const me = useMe()
  const isAdmin = me.data?.roles.includes('admin') ?? false
  const canControl = isAdmin || (me.data?.roles.includes('member') ?? false)
  const [viewer, setViewer] = useState<{ photos: PhotoRef[]; index: number } | null>(null)

  const list = devices.data ?? []
  const online = list.filter((d) => d.status === 'online').length
  const recent = events.data?.pages[0]?.items ?? []
  const openAlarms = recent.filter((e) => (e.severity === 'alarm' || e.severity === 'critical') && !e.acknowledgedAt).length

  return (
    <div className="space-y-4">
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
        <GuardCard className="sm:col-span-2 lg:col-span-1" />
        <Card>
          <CardContent>
            <p className="text-sm text-neutral-400">{t('overview.swarm')}</p>
            {devices.isLoading ? <Skeleton className="mt-2 h-8 w-20" /> : (
              <p className="mt-1 text-3xl font-semibold tabular-nums">
                {online}<span className="text-neutral-500">/{list.length}</span>
              </p>
            )}
            <p className="text-xs text-neutral-500">{t('overview.online')}</p>
          </CardContent>
        </Card>
        <Card className={cn(openAlarms > 0 && 'border-red-900/70 bg-red-950/30')}>
          <CardContent>
            <p className="text-sm text-neutral-400">{t('overview.unacknowledged')}</p>
            <p className={cn('mt-1 text-3xl font-semibold tabular-nums', openAlarms > 0 && 'text-red-400')}>{openAlarms}</p>
          </CardContent>
        </Card>
      </div>

      <WeatherCard />
      <Climate latest={latest.data ?? []} devices={list} zones={zones.data ?? []} loading={latest.isLoading} canControl={canControl} isAdmin={isAdmin} />
      <HiveState device={list.find((d) => d.type === 'hive')} latest={latest.data ?? []} loading={latest.isLoading} />

      <Card>
        <CardHeader>
          <CardTitle>{t('overview.latestEvents')}</CardTitle>
          <Link to="/events" className="flex items-center text-sm text-amber-400">
            {t('overview.allEvents')} <ChevronRight className="size-4" />
          </Link>
        </CardHeader>
        <CardContent className="space-y-2">
          {events.isLoading && <Skeleton className="h-20" />}
          {!events.isLoading && recent.length === 0 && <Empty>{t('events.empty')}</Empty>}
          {recent.map((e) => (
            <EventCard key={e.id} event={e} compact onOpenPhoto={(photos, index) => setViewer({ photos, index })} />
          ))}
        </CardContent>
      </Card>

      <Card>
        <CardHeader><CardTitle>{t('overview.devices')}</CardTitle></CardHeader>
        <CardContent>
          {!devices.isLoading && list.length === 0 && <Empty>{t('overview.noDevices')}</Empty>}
          <ul className="divide-y divide-neutral-800">
            {list.map((d) => <DeviceRow key={d.deviceId} device={d} />)}
          </ul>
        </CardContent>
      </Card>

      {viewer && (
        <Lightbox photos={viewer.photos} index={viewer.index} onIndex={(index) => setViewer({ ...viewer, index })} onClose={() => setViewer(null)} />
      )}
    </div>
  )
}

/** Every zone's tiles, as its card on the climate screen shows them (what each hornet is ticked to show). */
function Climate({ latest, devices, zones, loading, canControl, isAdmin }: {
  latest: LatestValueDto[]
  devices: DeviceDto[]
  zones: ZoneDto[]
  loading: boolean
  canControl: boolean
  isAdmin: boolean
}) {
  const hornets = devices.filter((d) => d.type !== 'hive')
  const known = new Set(zones.map((z) => z.id))
  const groups = [
    ...zones.map((z) => ({ key: String(z.id), title: z.name, devices: hornets.filter((d) => d.zoneId === z.id) })),
    { key: 'none', title: t('climate.unassigned'), devices: hornets.filter((d) => d.zoneId == null || !known.has(d.zoneId)) },
  ].filter((g) => zoneTiles(g.devices, latest).length > 0 || g.devices.some((d) => bannerKind(d, latest) != null))

  return (
    <Card>
      <CardHeader><CardTitle>{t('overview.climate')}</CardTitle></CardHeader>
      <CardContent className="space-y-3">
        {loading && <Skeleton className="h-16" />}
        {!loading && groups.length === 0 && <p className="text-sm text-neutral-500">{t('overview.noClimate')}</p>}
        {groups.map((g) => (
          <div key={g.key} className="space-y-1.5">
            {groups.length > 1 && <p className="text-xs text-neutral-500">{g.title}</p>}
            <ZoneTiles devices={g.devices} latest={latest} />
            <ZoneBanners devices={g.devices} latest={latest} canControl={canControl} isAdmin={isAdmin} />
          </div>
        ))}
      </CardContent>
    </Card>
  )
}

/** The hive's own readings (CPU temperature, disk use), reported by the hive as device hive-<node>. */
function HiveState({ device, latest, loading }: { device?: DeviceDto; latest: LatestValueDto[]; loading: boolean }) {
  if (!device) return null
  const own = latest.filter((v) => v.deviceId === device.deviceId && (v.metric === 'cpu_temperature' || v.metric === 'disk_used_pct'))
  return (
    <Card>
      <CardHeader>
        <CardTitle>{t('overview.hive')}</CardTitle>
        <Badge tone={device.status === 'online' ? 'ok' : 'alarm'}>{statusLabel(device.status)}</Badge>
      </CardHeader>
      <CardContent>
        {loading && <Skeleton className="h-16" />}
        {!loading && own.length === 0 && <p className="text-sm text-neutral-500">{t('overview.hiveNone')}</p>}
        <div className="grid grid-cols-2 gap-3">
          {own.map((v) => (
            <div key={v.metric} className="rounded-lg bg-neutral-800/50 p-3">
              <p className="truncate text-xs text-neutral-400">{metricLabel(v.metric)}</p>
              <p className="mt-1 text-2xl font-semibold tabular-nums">{formatValue(v.metric, v.value)}</p>
              <p className="text-xs text-neutral-500">{formatAgo(v.ts)}</p>
            </div>
          ))}
        </div>
      </CardContent>
    </Card>
  )
}

function DeviceRow({ device }: { device: DeviceDto }) {
  const online = device.status === 'online'
  return (
    <li className="flex items-center justify-between gap-3 py-2.5">
      <div className="min-w-0">
        <p className="truncate text-sm font-medium">{device.name}</p>
        <p className="truncate text-xs text-neutral-500">{device.type} · {device.fw ?? '—'} · {formatAgo(device.lastSeenAt)}</p>
      </div>
      <div className="flex shrink-0 items-center gap-2">
        {device.rssi != null && <span className="text-xs tabular-nums text-neutral-500">{device.rssi} dBm</span>}
        <Badge tone={online ? 'ok' : 'alarm'}>
          {online ? <Wifi className="size-3" /> : <WifiOff className="size-3" />}
          {statusLabel(device.status)}
        </Badge>
      </div>
    </li>
  )
}
