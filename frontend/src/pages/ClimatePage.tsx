import { useQueryClient } from '@tanstack/react-query'
import { Pencil, Plus, Power, PowerOff } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { useSearchParams } from 'react-router'
import { toast } from 'sonner'
import { api, call, type DeviceDto, type LatestValueDto, type ZoneDto } from '@/api/client'
import { historyWindow, keys, useDevices, useLatestTelemetry, useMe, useMetricSeries, useSendCommand, useWeather, useZones } from '@/api/queries'
import { MultiChart, TimeAxis, type ChartPoint, type ChartSeries } from '@/components/Chart'
import { ZoneEditor } from '@/components/ZoneEditor'
import { ZoneTiles } from '@/components/ZoneTiles'
import { Button } from '@/components/ui/button'
import { Badge, Card, CardContent, CardHeader, CardTitle, Empty, Input, Select, Skeleton } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { readingTitle, shownKeys, shownReadings, zoneKinds, zoneTiles } from '@/lib/dashboard'
import { formatFixed, formatValue, metricUnit } from '@/lib/format'
import { ranges, rangeLabel } from '@/lib/ranges'
import { colorsByDevice } from '@/lib/seriesColors'
import { hasRelay } from '@/lib/features'

const forecastColor = { stroke: 'stroke-neutral-400', fill: 'fill-neutral-400', bg: 'bg-neutral-400' }

/** Screen 6 (spec 8.2): the climate of every zone, with the forecast alongside outdoors, and the heaters in it. */
export function ClimatePage() {
  const [hours, setHours] = useState(24)
  // The zone editor is in the address (?edit=3), so the way back from a hornet's card opens it again.
  const [params, setParams] = useSearchParams()
  const zones = useZones()
  const devices = useDevices()
  const latest = useLatestTelemetry()
  const me = useMe()
  const isAdmin = me.data?.roles.includes('admin') ?? false
  const canControl = isAdmin || (me.data?.roles.includes('member') ?? false)
  const list = (devices.data ?? []).filter((d) => d.type !== 'hive')
  const range = historyWindow(hours)
  const editing = isAdmin ? (zones.data ?? []).find((z) => String(z.id) === params.get('edit')) : undefined
  const edit = (id: number | null) => setParams(id == null ? {} : { edit: String(id) }, { replace: true })

  // A hornet "has climate" when it reports a temperature, or is a heater.
  const hasTemperature = (d: DeviceDto) => (latest.data ?? []).some((v) => v.deviceId === d.deviceId && v.metric === 'temperature')
  const orphans = list.filter((d) => d.zoneId == null && (hasTemperature(d) || d.type === 'heat'))

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between gap-3">
        <h1 className="text-lg font-semibold">{t('climate.title')}</h1>
        <Select value={hours} onChange={(e) => setHours(Number(e.target.value))} className="h-8 w-auto text-xs" aria-label={t('device.range')}>
          {ranges.map((h) => <option key={h} value={h}>{rangeLabel(h)}</option>)}
        </Select>
      </div>

      {zones.isLoading && <Skeleton className="h-40" />}
      {!zones.isLoading && (zones.data ?? []).length === 0 && <Empty>{t('climate.noZones')}</Empty>}
      {(zones.data ?? []).map((zone) => (
        <ZoneCard
          key={zone.id}
          zone={zone}
          devices={list.filter((d) => d.zoneId === zone.id)}
          latest={latest.data ?? []}
          hours={hours}
          isAdmin={isAdmin}
          canControl={canControl}
          onEdit={() => edit(zone.id)}
        />
      ))}

      {isAdmin && orphans.length > 0 && (
        <Card>
          <CardHeader><CardTitle>{t('climate.unassigned')}</CardTitle></CardHeader>
          <CardContent className="space-y-2">
            <p className="text-xs text-neutral-500">{t('climate.unassignedHint')}</p>
            {orphans.map((d) => (
              <div key={d.deviceId} className="flex items-center justify-between gap-3">
                <span className="min-w-0 truncate text-sm">{d.name}</span>
                <ZoneSelect device={d} zones={zones.data ?? []} />
              </div>
            ))}
          </CardContent>
        </Card>
      )}

      {isAdmin && <AddZone />}
      {(zones.data ?? []).length > 0 && <TimeAxis {...range} />}
      {editing && <ZoneEditor zone={editing} devices={list} latest={latest.data ?? []} onClose={() => edit(null)} />}
    </div>
  )
}

/** Assigns a hornet to a zone (admin). */
export function ZoneSelect({ device, zones }: { device: DeviceDto; zones: ZoneDto[] }) {
  const qc = useQueryClient()
  async function assign(value: string) {
    try {
      await call(api.PUT('/api/devices/{deviceId}/zone', { params: { path: { deviceId: device.deviceId } }, body: { zoneId: value === '' ? null : Number(value) } }))
      await Promise.all([qc.invalidateQueries({ queryKey: keys.devices }), qc.invalidateQueries({ queryKey: ['zones'] })])
    } catch {
      toast.error(t('climate.failed'))
    }
  }
  return (
    <Select value={device.zoneId ?? ''} onChange={(e) => void assign(e.target.value)} className="h-8 w-auto text-xs" aria-label={t('climate.zone')}>
      <option value="">{t('climate.noZone')}</option>
      {zones.map((z) => <option key={z.id} value={z.id}>{z.name}</option>)}
    </Select>
  )
}

function ZoneCard({ zone, devices, latest, hours, isAdmin, canControl, onEdit }: {
  zone: ZoneDto
  devices: DeviceDto[]
  latest: LatestValueDto[]
  hours: number
  isAdmin: boolean
  canControl: boolean
  onEdit: () => void
}) {
  const weather = useWeather()
  // Lines are what each hornet is ticked to show: a bridge's t2 and t3 are two lines, its t1 none when unticked.
  const readings = devices.flatMap((d) => shownReadings(d, latest).map((v) => ({ device: d, metric: v.metric })))
  const tempLines = readings.filter((r) => r.metric.startsWith('temperature'))
  const humidityLines = readings.filter((r) => r.metric === 'humidity')
  const heaters = devices.filter((d) => (d.type === 'heat' || d.type === 'relay') && hasRelay(d.features) && shownKeys(d, latest).has('relay'))
  const temperature = useMetricSeries(tempLines.map((r) => ({ device: r.device.deviceId, metric: r.metric })), hours)
  const humidity = useMetricSeries(humidityLines.map((r) => ({ device: r.device.deviceId, metric: r.metric })), hours)
  const { from, to } = historyWindow(hours)

  // Colours go by hornet, not by line number: the humidity of the third thermometer is drawn in its colour.
  // A hornet with several probes gives each probe a colour of its own.
  const lineKey = (r: { device: DeviceDto; metric: string }) => (r.metric === 'humidity' ? r.device.deviceId : `${r.device.deviceId}/${r.metric}`)
  const humidityKey = (d: DeviceDto) => {
    const first = tempLines.find((r) => r.device === d)
    return first ? lineKey(first) : d.deviceId
  }
  const colorOf = colorsByDevice([...tempLines.map(lineKey), ...humidityLines.map((r) => humidityKey(r.device))])
  const tempSeriesOf: ChartSeries[] = tempLines.map((r, i) => ({ name: readingTitle(r.device, r.metric, latest), color: colorOf(lineKey(r)), points: temperature[i]?.data ?? [] }))
  const humiditySeries: ChartSeries[] = humidityLines.map((r, i) => ({ name: r.device.name, color: colorOf(humidityKey(r.device)), points: humidity[i]?.data ?? [] }))

  // Outdoors, the forecast runs alongside the sensors: what was expected against what happened, and what comes next.
  const forecast: ChartPoint[] = zone.kind === 'garden'
    ? (weather.data?.hourly ?? [])
        .filter((h) => new Date(h.time) >= from && new Date(h.time) <= to)
        .map((h) => ({ ts: h.time, avg: h.temperature, min: h.temperature, max: h.temperature }))
    : []
  const tempSeries = [...tempSeriesOf, ...(forecast.length > 0 ? [{ name: t('climate.forecast'), color: forecastColor, dashed: true, points: forecast }] : [])]
  const all = temperature.flatMap((q) => q.data ?? [])
  const lo = all.length ? Math.min(...all.map((p) => p.min)) : null
  const hi = all.length ? Math.max(...all.map((p) => p.max)) : null
  const tiles = zoneTiles(devices, latest)

  return (
    <Card>
      <CardHeader>
        <CardTitle>{zone.name} <span className="ml-1 text-xs font-normal text-neutral-500">{t(`climate.kinds.${zone.kind}`)}</span></CardTitle>
        {isAdmin && <Button variant="ghost" size="icon" title={t('climate.editZone').replace('{name}', zone.name)} onClick={onEdit}><Pencil /></Button>}
      </CardHeader>
      <CardContent className="space-y-4">
        {tiles.length === 0 && heaters.length === 0 && <p className="text-sm text-neutral-500">{t('climate.noSensors')}</p>}

        <ZoneTiles devices={devices} latest={latest} editZone={isAdmin ? zone.id : undefined} />

        {tempLines.length > 0 && (
          <div className="space-y-1">
            <p className="text-sm font-medium">{t('climate.temperature')}</p>
            <MultiChart series={tempSeries} from={from} to={to} format={(v) => formatValue('temperature', v)} axisFormat={(v, d) => `${formatFixed(v, d)}${metricUnit('temperature')}`} />
            {lo != null && hi != null && <p className="text-xs text-neutral-500">{t('climate.minMax').replace('{min}', formatValue('temperature', lo)).replace('{max}', formatValue('temperature', hi))}</p>}
          </div>
        )}

        {humidityLines.length > 0 && (
          <div className="space-y-1">
            <p className="text-sm font-medium">{t('climate.humidity')}</p>
            <MultiChart series={humiditySeries} from={from} to={to} format={(v) => formatValue('humidity', v)} axisFormat={(v, d) => `${formatFixed(v, d)}${metricUnit('humidity')}`} />
          </div>
        )}

        {heaters.length > 0 && (
          <div className="space-y-2">
            <p className="text-sm font-medium">{t('climate.heating')}</p>
            {heaters.map((d) => <HeaterRow key={d.deviceId} device={d} canControl={canControl} />)}
          </div>
        )}
      </CardContent>
    </Card>
  )
}

/** A heater or relay: its state, and on-for-an-hour / off (manual control; the hornet keeps its own safety limits). */
function HeaterRow({ device, canControl }: { device: DeviceDto; canControl: boolean }) {
  const send = useSendCommand(device.deviceId)
  const state = (device.state as Record<string, unknown> | null) ?? {}
  const channel = Object.keys(state)[0] ?? (device.type === 'heat' && device.features?.includes('heater') !== false ? 'heater' : 'relay')
  const value = state[channel]
  const online = device.status === 'online'
  return (
    <div className="flex items-center justify-between gap-3 rounded-lg bg-neutral-800/50 p-2.5">
      <div className="min-w-0">
        <p className="truncate text-sm">{device.name}</p>
        <Badge tone={value === 'on' ? 'ok' : 'neutral'}>{value === 'on' ? t('climate.heaterOn') : value === 'off' ? t('climate.heaterOff') : t('climate.heaterUnknown')}</Badge>
      </div>
      {canControl && online && (
        <div className="flex shrink-0 gap-2">
          <Button size="sm" variant="outline" onClick={() => void send('relay', { channel, set: 'on', for_s: 3600 })}><Power />{t('climate.heaterOnHour')}</Button>
          <Button size="sm" variant="outline" onClick={() => void send('relay', { channel, set: 'off' })}><PowerOff />{t('climate.heaterOff2')}</Button>
        </div>
      )}
    </div>
  )
}

function AddZone() {
  const qc = useQueryClient()
  const [name, setName] = useState('')
  const [kind, setKind] = useState<(typeof zoneKinds)[number]>('house')

  async function add(e: FormEvent) {
    e.preventDefault()
    const response = await api.POST('/api/zones', { body: { name: name.trim(), kind } })
    if (response.response.status === 409) toast.error(t('climate.exists'))
    else if (!response.response.ok) toast.error(t('climate.failed'))
    else {
      setName('')
      toast.success(t('climate.saved'))
    }
    await qc.invalidateQueries({ queryKey: ['zones'] })
  }

  return (
    <Card>
      <CardHeader><CardTitle>{t('climate.addZone')}</CardTitle></CardHeader>
      <CardContent>
        <form onSubmit={add} className="flex flex-wrap items-center gap-2">
          <Input className="min-w-40 flex-1" placeholder={t('climate.zoneName')} value={name} onChange={(e) => setName(e.target.value)} />
          <Select value={kind} onChange={(e) => setKind(e.target.value as (typeof zoneKinds)[number])} className="w-auto">
            {zoneKinds.map((k) => <option key={k} value={k}>{t(`climate.kinds.${k}`)}</option>)}
          </Select>
          <Button type="submit" disabled={!name.trim()}><Plus />{t('climate.addZone')}</Button>
        </form>
      </CardContent>
    </Card>
  )
}
