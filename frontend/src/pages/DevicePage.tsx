import { ArrowLeft, Camera, Power, PowerOff, RotateCw, Wifi, WifiOff } from 'lucide-react'
import { useState } from 'react'
import { Link, useParams } from 'react-router'
import type { DeviceDto } from '@/api/client'
import { historyWindow, useZones, useCommandHistory, useDeviceLogs, useDevices, useHealthHistory, useLatestTelemetry, useMe, useSendCommand, useSeries } from '@/api/queries'
import { Chart, TimeAxis, type ChartPoint } from '@/components/Chart'
import { Button } from '@/components/ui/button'
import { Badge, Card, CardContent, CardHeader, CardTitle, Empty, Select, Skeleton } from '@/components/ui/primitives'
import { metricLabel, statusLabel, t } from '@/i18n'
import { formatAgo, formatFixed, formatValue, formatWhen, metricUnit } from '@/lib/format'
import { ranges, rangeLabel } from '@/lib/ranges'
import { ZoneSelect } from '@/pages/ClimatePage'
import { PinsPanel } from '@/pages/SwarmPage'

function MetricChart({ device, metric, hours }: { device: string; metric: string; hours: number }) {
  const series = useSeries(device, metric, hours)
  const { from, to } = historyWindow(hours)
  return (
    <div className="space-y-1">
      <p className="text-sm font-medium">{metricLabel(metric)}</p>
      {series.isLoading ? <Skeleton className="h-36" /> : (
        <Chart
          points={series.data ?? []}
          from={from}
          to={to}
          format={(v) => formatValue(metric, v)}
          axisFormat={(v, d) => `${formatFixed(v, d)}${metricUnit(metric)}`}
        />
      )}
    </div>
  )
}

/** Signal, free memory and battery from the hornet's own health reports (a battery line only when it reports one). */
function HealthCharts({ device, hours }: { device: string; hours: number }) {
  const health = useHealthHistory(device, hours)
  const { from, to } = historyWindow(hours)
  if (health.isLoading) return <Skeleton className="h-36" />
  const rows = health.data ?? []
  const line = (pick: (r: (typeof rows)[number]) => number | null | undefined): ChartPoint[] =>
    rows.flatMap((r) => {
      const v = pick(r)
      return v == null ? [] : [{ ts: r.ts, avg: v, min: v, max: v }]
    })
  const battery = line((r) => r.vbat)
  return (
    <>
      <div className="space-y-1">
        <p className="text-sm font-medium">{t('device.signal')}</p>
        <Chart points={line((r) => r.rssi)} from={from} to={to} format={(v) => `${Math.round(v)} dBm`} axisFormat={(v, d) => formatFixed(v, d)} />
      </div>
      <div className="space-y-1">
        <p className="text-sm font-medium">{t('device.memory')}</p>
        <Chart points={line((r) => r.heapFree / 1024)} from={from} to={to} format={(v) => `${Math.round(v)} KB`} axisFormat={(v, d) => formatFixed(v, d)} />
      </div>
      {battery.length > 0 && (
        <div className="space-y-1">
          <p className="text-sm font-medium">{t('device.battery')}</p>
          <Chart points={battery} from={from} to={to} format={(v) => `${v.toFixed(2)} V`} axisFormat={(v, d) => formatFixed(v, d)} />
        </div>
      )}
    </>
  )
}

function CommandHistory({ device }: { device: string }) {
  const history = useCommandHistory(device, true)
  const rows = history.data ?? []
  return (
    <Card>
      <CardHeader><CardTitle>{t('device.commandHistory')}</CardTitle></CardHeader>
      <CardContent>
        {history.isLoading && <Skeleton className="h-16" />}
        {!history.isLoading && rows.length === 0 && <p className="text-sm text-neutral-500">{t('device.noCommands')}</p>}
        <ul className="divide-y divide-neutral-800">
          {rows.map((c) => (
            <li key={c.cid} className="flex items-center justify-between gap-3 py-2 text-sm">
              <div className="min-w-0">
                <p className="truncate font-medium">{c.name}</p>
                <p className="truncate text-xs text-neutral-500">{c.issuedBy} · {formatWhen(c.issuedAt)}</p>
              </div>
              <Badge tone={c.status === 'ok' ? 'ok' : c.status === 'sent' ? 'neutral' : 'alarm'}>{t(`device.commandStatus.${c.status}`, c.status)}</Badge>
            </li>
          ))}
        </ul>
      </CardContent>
    </Card>
  )
}

const logLevels = ['', 'error', 'warn', 'info', 'debug']

function Logs({ device }: { device: string }) {
  const [level, setLevel] = useState('')
  const logs = useDeviceLogs(device, level)
  const rows = logs.data ?? []
  return (
    <Card>
      <CardHeader>
        <CardTitle>{t('device.logs')}</CardTitle>
        <Select value={level} onChange={(e) => setLevel(e.target.value)} className="h-8 w-auto text-xs" aria-label={t('device.logLevel')}>
          {logLevels.map((l) => <option key={l} value={l}>{l === '' ? t('device.logAll') : l}</option>)}
        </Select>
      </CardHeader>
      <CardContent>
        {logs.isLoading && <Skeleton className="h-16" />}
        {!logs.isLoading && rows.length === 0 && <p className="text-sm text-neutral-500">{t('device.noLogs')}</p>}
        <ul className="space-y-1 font-mono text-xs">
          {rows.map((l) => (
            <li key={l.id} className="flex gap-2">
              <span className="shrink-0 text-neutral-500">{formatWhen(l.ts)}</span>
              <span className={l.level === 'error' ? 'shrink-0 text-red-400' : l.level === 'warn' ? 'shrink-0 text-amber-400' : 'shrink-0 text-neutral-400'}>{l.level}</span>
              <span className="min-w-0 break-words">{l.msg}</span>
            </li>
          ))}
        </ul>
      </CardContent>
    </Card>
  )
}

/** Command names the hornet announced in its info message (contracts/schemas/info.schema.json "commands"). */
function announcedCommands(d: DeviceDto): string[] {
  const info = d.info as { commands?: unknown } | null
  return Array.isArray(info?.commands) ? info.commands.filter((c): c is string => typeof c === 'string') : []
}

/** Relay channel names come from the hornet's retained state ({channel: on|off}); "relay" is the firmware default. */
function relayChannels(d: DeviceDto): string[] {
  const keys = Object.keys((d.state as Record<string, unknown> | null) ?? {})
  return keys.length > 0 ? keys : ['relay']
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="min-w-0">
      <dt className="text-xs text-neutral-500">{label}</dt>
      <dd className="truncate text-sm tabular-nums">{children ?? '—'}</dd>
    </div>
  )
}

/** Screen 4 (spec 8.2): one hornet's status, readings, commands and pins. Charts and logs need history endpoints. */
export function DevicePage() {
  const { deviceId = '' } = useParams()
  const [hours, setHours] = useState(24)
  const devices = useDevices()
  const zones = useZones()
  const latest = useLatestTelemetry()
  const me = useMe()
  const canControl = (me.data?.roles.includes('admin') ?? false) || (me.data?.roles.includes('member') ?? false)
  const isAdmin = me.data?.roles.includes('admin') ?? false
  const device = (devices.data ?? []).find((d) => d.deviceId === deviceId)
  const readings = (latest.data ?? []).filter((v) => v.deviceId === deviceId).sort((a, b) => a.metric.localeCompare(b.metric))

  const send = useSendCommand(deviceId)

  const back = (
    <Link to="/swarm" className="flex items-center gap-1 text-sm text-amber-400">
      <ArrowLeft className="size-4" />{t('device.back')}
    </Link>
  )

  if (devices.isLoading) return <Skeleton className="h-60" />
  if (!device) return <div className="space-y-3">{back}<Empty>{t('device.notFound')}</Empty></div>

  const commands = announcedCommands(device)
  const online = device.status === 'online'
  const configPending = device.configRev !== device.configAppliedRev

  return (
    <div className="space-y-4">
      {back}
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <h1 className="truncate text-lg font-semibold">{device.name}</h1>
          <p className="truncate text-xs text-neutral-500">{device.deviceId} · {device.type}</p>
        </div>
        <Badge tone={online ? 'ok' : device.status === 'disabled' || device.status === 'pending' ? 'neutral' : 'alarm'}>
          {online ? <Wifi className="size-3" /> : <WifiOff className="size-3" />}
          {statusLabel(device.status)}
        </Badge>
      </div>

      <Card>
        <CardHeader><CardTitle>{t('device.about')}</CardTitle></CardHeader>
        <CardContent>
          <dl className="grid grid-cols-2 gap-3 sm:grid-cols-3">
            <Field label={t('device.zone')}>{isAdmin ? <ZoneSelect device={device} zones={zones.data ?? []} /> : device.zone}</Field>
            <Field label={t('device.board')}>{device.hw}</Field>
            <Field label={t('device.firmware')}>{device.fw}</Field>
            <Field label="MAC">{device.mac}</Field>
            <Field label="IP">{device.ip}</Field>
            <Field label={t('device.signal')}>{device.rssi != null ? `${device.rssi} dBm` : null}</Field>
            <Field label={t('device.lastSeen')}>{formatAgo(device.lastSeenAt)}</Field>
            <Field label={t('device.boots')}>{device.boot}</Field>
            <Field label={t('device.config')}>
              {configPending ? <span className="text-amber-400">{t('device.configPending')}</span> : t('device.configApplied')}
            </Field>
          </dl>
        </CardContent>
      </Card>

      <Card>
        <CardHeader><CardTitle>{t('device.readings')}</CardTitle></CardHeader>
        <CardContent>
          {latest.isLoading && <Skeleton className="h-16" />}
          {!latest.isLoading && readings.length === 0 && <p className="text-sm text-neutral-500">{t('overview.noClimate')}</p>}
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
            {readings.map((v) => (
              <div key={v.metric} className="rounded-lg bg-neutral-800/50 p-3">
                <p className="truncate text-xs text-neutral-400">{metricLabel(v.metric)}</p>
                <p className="mt-1 text-2xl font-semibold tabular-nums">{formatValue(v.metric, v.value)}</p>
                <p className="text-xs text-neutral-500">{formatAgo(v.ts)}</p>
              </div>
            ))}
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{t('device.history')}</CardTitle>
          <Select value={hours} onChange={(e) => setHours(Number(e.target.value))} className="h-8 w-auto text-xs" aria-label={t('device.range')}>
            {ranges.map((h) => <option key={h} value={h}>{rangeLabel(h)}</option>)}
          </Select>
        </CardHeader>
        <CardContent className="space-y-5">
          {readings.map((v) => <MetricChart key={v.metric} device={deviceId} metric={v.metric} hours={hours} />)}
          <HealthCharts device={deviceId} hours={hours} />
          <TimeAxis {...historyWindow(hours)} />
        </CardContent>
      </Card>

      {canControl && online && (commands.includes('snapshot') || commands.includes('relay') || commands.includes('reboot')) && (
        <Card>
          <CardHeader><CardTitle>{t('device.commands')}</CardTitle></CardHeader>
          <CardContent className="flex flex-wrap gap-2">
            {commands.includes('snapshot') && (
              <Button variant="outline" onClick={() => void send('snapshot', { count: 1 })}><Camera />{t('device.snapshot')}</Button>
            )}
            {commands.includes('relay') && relayChannels(device).map((channel) => (
              <div key={channel} className="flex items-center gap-2">
                <span className="text-xs text-neutral-500">{channel}</span>
                <Button variant="outline" onClick={() => void send('relay', { channel, set: 'on' })}><Power />{t('device.relayOn')}</Button>
                <Button variant="outline" onClick={() => void send('relay', { channel, set: 'off' })}><PowerOff />{t('device.relayOff')}</Button>
              </div>
            ))}
            {commands.includes('reboot') && (
              <Button variant="outline" onClick={() => void send('reboot')}><RotateCw />{t('device.reboot')}</Button>
            )}
          </CardContent>
        </Card>
      )}

      {canControl && <CommandHistory device={deviceId} />}
      <Logs device={deviceId} />

      {device.status !== 'disabled' && (
        <Card>
          <CardHeader><CardTitle>{t('swarm.pins')}</CardTitle></CardHeader>
          <CardContent><PinsPanel deviceId={device.deviceId} editable={isAdmin} board={device.hw} type={device.type} /></CardContent>
        </Card>
      )}
    </div>
  )
}
