import { useQueryClient } from '@tanstack/react-query'
import { ChevronRight, LayoutGrid, Monitor, Smartphone, Wifi, WifiOff } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router'
import { toast } from 'sonner'
import { api, call, type DeviceDto, type LatestValueDto, type ZoneDto } from '@/api/client'
import { useDashboardLayout, useDevices, useEvents, useLatestTelemetry, useMe, useZones } from '@/api/queries'
import { DashboardGrid, type WidgetView } from '@/components/DashboardGrid'
import { CameraBanner, RelayBanner } from '@/components/DeviceBanners'
import { EventCard, type PhotoRef } from '@/components/EventCard'
import { GuardCard } from '@/components/GuardCard'
import { Lightbox } from '@/components/Lightbox'
import { WeatherCard } from '@/components/WeatherCard'
import { ZoneTiles } from '@/components/ZoneTiles'
import { Button } from '@/components/ui/button'
import { Badge, Card, CardContent, CardHeader, CardTitle, Empty, Skeleton } from '@/components/ui/primitives'
import { metricLabel, statusLabel, t } from '@/i18n'
import { bannerKind, shownKeys, zoneTiles } from '@/lib/dashboard'
import { formatAgo, formatValue } from '@/lib/format'
import { arrange, useWideScreen, type Available, type Screen, type Widget } from '@/lib/layout'
import { cn } from '@/lib/utils'

/**
 * Screen 1 (spec 8.2): guard, swarm health, weather, each zone's readings, cameras and relays, the hive, latest
 * events. The widgets are arranged apart for a wide screen and for a phone (an administrator moves, sizes and hides
 * them in "Настроить").
 */
export function OverviewPage() {
  const qc = useQueryClient()
  const devices = useDevices()
  const latest = useLatestTelemetry()
  const events = useEvents({ limit: 5 })
  const zones = useZones()
  const saved = useDashboardLayout()
  const me = useMe()
  const wide = useWideScreen()
  const isAdmin = me.data?.roles.includes('admin') ?? false
  const canControl = isAdmin || (me.data?.roles.includes('member') ?? false)
  const [viewer, setViewer] = useState<{ photos: PhotoRef[]; index: number } | null>(null)
  // While arranging: which screen, and the arrangement not saved yet.
  const [editing, setEditing] = useState<{ screen: Screen; layout: Widget[] } | null>(null)

  const list = devices.data ?? []
  const values = latest.data ?? []
  const online = list.filter((d) => d.status === 'online').length
  const recent = events.data?.pages[0]?.items ?? []
  const openAlarms = recent.filter((e) => (e.severity === 'alarm' || e.severity === 'critical') && !e.acknowledgedAt).length
  const hive = list.find((d) => d.type === 'hive')

  const views: (WidgetView & { w: number })[] = [
    { id: 'guard', w: 4, title: t('layout.widgets.guard'), node: <GuardCard className="h-full" /> },
    {
      id: 'swarm', w: 4, title: t('overview.swarm'), node: (
        <Card className="h-full">
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
      ),
    },
    {
      id: 'alarms', w: 4, title: t('overview.unacknowledged'), node: (
        <Card className={cn('h-full', openAlarms > 0 && 'border-red-900/70 bg-red-950/30')}>
          <CardContent>
            <p className="text-sm text-neutral-400">{t('overview.unacknowledged')}</p>
            <p className={cn('mt-1 text-3xl font-semibold tabular-nums', openAlarms > 0 && 'text-red-400')}>{openAlarms}</p>
          </CardContent>
        </Card>
      ),
    },
    { id: 'weather', w: 12, title: t('layout.widgets.weather'), node: <WeatherCard /> },
    ...zoneViews(list, zones.data ?? [], values),
    ...list.flatMap((d) => {
      const kind = bannerKind(d, values)
      if (!kind) return []
      return [{
        id: `${kind === 'camera' ? 'cam' : 'relay'}:${d.deviceId}`,
        w: 4,
        title: d.name,
        node: (
          <Card className="h-full p-2">
            {kind === 'camera'
              ? <CameraBanner device={d} shown={shownKeys(d, values)} canControl={canControl} />
              : <RelayBanner device={d} canControl={canControl} isAdmin={isAdmin} />}
          </Card>
        ),
      }]
    }),
    ...(hive ? [{ id: 'hive', w: 6, title: t('overview.hive'), node: <HiveState device={hive} latest={values} loading={latest.isLoading} /> }] : []),
    {
      id: 'events', w: 6, title: t('overview.latestEvents'), node: (
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
      ),
    },
    {
      id: 'devices', w: 6, title: t('overview.devices'), node: (
        <Card>
          <CardHeader><CardTitle>{t('overview.devices')}</CardTitle></CardHeader>
          <CardContent>
            {!devices.isLoading && list.length === 0 && <Empty>{t('overview.noDevices')}</Empty>}
            <ul className="divide-y divide-neutral-800">
              {list.map((d) => <DeviceRow key={d.deviceId} device={d} />)}
            </ul>
          </CardContent>
        </Card>
      ),
    },
  ]
  const available: Available[] = views.map((v) => ({ id: v.id, w: v.w }))
  const layoutOf = (screen: Screen) => arrange(saved.data?.[screen], available)
  const screen: Screen = editing?.screen ?? (wide ? 'desktop' : 'phone')
  const layout = editing?.layout ?? layoutOf(screen)

  async function save() {
    if (!editing) return
    try {
      await call(api.PUT('/api/dashboard/layout/{screen}', { params: { path: { screen: editing.screen } }, body: { items: editing.layout } }))
      await qc.invalidateQueries({ queryKey: ['dashboard', 'layout'] })
      toast.success(t('layout.saved'))
      setEditing(null)
    } catch {
      toast.error(t('layout.failed'))
    }
  }

  async function reset() {
    if (!editing || !window.confirm(t('layout.resetConfirm'))) return
    await call(api.PUT('/api/dashboard/layout/{screen}', { params: { path: { screen: editing.screen } }, body: { items: null } }))
    await qc.invalidateQueries({ queryKey: ['dashboard', 'layout'] })
    setEditing(null)
  }

  return (
    <div className="space-y-3">
      {isAdmin && (editing ? (
        <div className="flex flex-wrap items-center gap-2 rounded-xl border border-amber-400/40 bg-amber-400/5 p-2">
          <div className="flex rounded-lg border border-neutral-700 p-0.5">
            {(['desktop', 'phone'] as const).map((s) => (
              <button
                key={s}
                type="button"
                className={cn('flex items-center gap-1.5 rounded-md px-2.5 py-1 text-xs', editing.screen === s ? 'bg-neutral-800 text-neutral-100' : 'text-neutral-400')}
                onClick={() => setEditing({ screen: s, layout: layoutOf(s) })}
              >
                {s === 'desktop' ? <Monitor className="size-3.5" /> : <Smartphone className="size-3.5" />}{t(`layout.screens.${s}`)}
              </button>
            ))}
          </div>
          <p className="min-w-0 flex-1 text-xs text-neutral-400">{t('layout.hint')}</p>
          <Button size="sm" variant="ghost" onClick={() => void reset()}>{t('layout.reset')}</Button>
          <Button size="sm" variant="outline" onClick={() => setEditing(null)}>{t('layout.cancel')}</Button>
          <Button size="sm" onClick={() => void save()}>{t('layout.save')}</Button>
        </div>
      ) : (
        <div className="flex justify-end">
          <Button size="sm" variant="ghost" className="text-neutral-400" onClick={() => setEditing({ screen, layout: layoutOf(screen) })}>
            <LayoutGrid />{t('layout.arrange')}
          </Button>
        </div>
      ))}

      <DashboardGrid
        views={views}
        layout={layout}
        phone={screen === 'phone'}
        editing={editing != null}
        onChange={(next) => editing && setEditing({ ...editing, layout: next })}
      />

      {viewer && (
        <Lightbox photos={viewer.photos} index={viewer.index} onIndex={(index) => setViewer({ ...viewer, index })} onClose={() => setViewer(null)} />
      )}
    </div>
  )
}

/** A widget per zone with readings (what each hornet is ticked to show), and one for hornets without a zone. */
function zoneViews(devices: DeviceDto[], zones: ZoneDto[], latest: LatestValueDto[]): (WidgetView & { w: number })[] {
  const hornets = devices.filter((d) => d.type !== 'hive')
  const known = new Set(zones.map((z) => z.id))
  return [
    ...zones.map((z) => ({ id: `zone:${z.id}`, title: z.name, devices: hornets.filter((d) => d.zoneId === z.id) })),
    { id: 'zone:none', title: t('climate.unassigned'), devices: hornets.filter((d) => d.zoneId == null || !known.has(d.zoneId)) },
  ]
    .filter((g) => zoneTiles(g.devices, latest).length > 0)
    .map((g) => ({
      id: g.id,
      w: 6,
      title: g.title,
      node: (
        <Card className="h-full">
          <CardHeader><CardTitle>{g.title}</CardTitle></CardHeader>
          <CardContent><ZoneTiles devices={g.devices} latest={latest} /></CardContent>
        </Card>
      ),
    }))
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
