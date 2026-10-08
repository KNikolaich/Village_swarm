import { ChevronRight, Thermometer, Wifi, WifiOff } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router'
import type { DeviceDto, LatestValueDto } from '@/api/client'
import { useDevices, useEvents, useLatestTelemetry } from '@/api/queries'
import { EventCard, type PhotoRef } from '@/components/EventCard'
import { GuardCard } from '@/components/GuardCard'
import { Lightbox } from '@/components/Lightbox'
import { Badge, Card, CardContent, CardHeader, CardTitle, Empty, Skeleton } from '@/components/ui/primitives'
import { metricLabel, statusLabel, t } from '@/i18n'
import { formatAgo, formatValue } from '@/lib/format'
import { cn } from '@/lib/utils'

/** Screen 1 (spec 8.2): swarm health, temperatures, latest events with photo previews. */
export function OverviewPage() {
  const devices = useDevices()
  const latest = useLatestTelemetry()
  const events = useEvents({ limit: 5 })
  const [viewer, setViewer] = useState<{ photos: PhotoRef[]; index: number } | null>(null)

  const list = devices.data ?? []
  const online = list.filter((d) => d.status === 'online').length
  const recent = events.data?.pages[0]?.items ?? []
  const openAlarms = recent.filter((e) => (e.severity === 'alarm' || e.severity === 'critical') && !e.acknowledgedAt).length

  return (
    <div className="space-y-4">
      <GuardCard />
      <div className="grid grid-cols-2 gap-3 sm:grid-cols-3">
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

      <Climate latest={latest.data ?? []} devices={list} loading={latest.isLoading} />

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

function Climate({ latest, devices, loading }: { latest: LatestValueDto[]; devices: DeviceDto[]; loading: boolean }) {
  const temps = latest.filter((v) => v.metric === 'temperature').sort((a, b) => a.deviceId.localeCompare(b.deviceId))
  const name = (id: string) => devices.find((d) => d.deviceId === id)?.name ?? id
  const humidity = (id: string) => latest.find((v) => v.deviceId === id && v.metric === 'humidity')

  return (
    <Card>
      <CardHeader><CardTitle>{t('overview.climate')}</CardTitle></CardHeader>
      <CardContent>
        {loading && <Skeleton className="h-16" />}
        {!loading && temps.length === 0 && <p className="text-sm text-neutral-500">{t('overview.noClimate')}</p>}
        <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
          {temps.map((v) => {
            const h = humidity(v.deviceId)
            return (
              <div key={v.deviceId} className="rounded-lg bg-neutral-800/50 p-3">
                <p className="flex items-center gap-1 truncate text-xs text-neutral-400"><Thermometer className="size-3.5" />{name(v.deviceId)}</p>
                <p className={cn('mt-1 text-2xl font-semibold tabular-nums', v.value < 0 ? 'text-sky-300' : v.value > 28 ? 'text-orange-300' : '')}>
                  {formatValue('temperature', v.value)}
                </p>
                <p className="text-xs text-neutral-500">
                  {h ? `${metricLabel('humidity')} ${formatValue('humidity', h.value)} · ` : ''}{formatAgo(v.ts)}
                </p>
              </div>
            )
          })}
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
