import { ArrowLeft, Camera, Power, PowerOff, RotateCw, Wifi, WifiOff } from 'lucide-react'
import { Link, useParams } from 'react-router'
import { toast } from 'sonner'
import { api, call, type DeviceDto } from '@/api/client'
import { useDevices, useLatestTelemetry, useMe } from '@/api/queries'
import { Button } from '@/components/ui/button'
import { Badge, Card, CardContent, CardHeader, CardTitle, Empty, Skeleton } from '@/components/ui/primitives'
import { metricLabel, statusLabel, t } from '@/i18n'
import { formatAgo, formatValue } from '@/lib/format'
import { PinsPanel } from '@/pages/SwarmPage'

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
  const devices = useDevices()
  const latest = useLatestTelemetry()
  const me = useMe()
  const canControl = (me.data?.roles.includes('admin') ?? false) || (me.data?.roles.includes('member') ?? false)
  const isAdmin = me.data?.roles.includes('admin') ?? false
  const device = (devices.data ?? []).find((d) => d.deviceId === deviceId)
  const readings = (latest.data ?? []).filter((v) => v.deviceId === deviceId).sort((a, b) => a.metric.localeCompare(b.metric))

  async function send(name: string, payload?: Record<string, unknown>) {
    try {
      await call(api.POST('/api/devices/{deviceId}/commands', { params: { path: { deviceId } }, body: { name, payload: payload as Record<string, never> | undefined } }))
      toast.success(t('device.commandSent'))
    } catch {
      toast.error(t('device.commandRefused'))
    }
  }

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
            <Field label={t('device.zone')}>{device.zone}</Field>
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

      {device.status !== 'disabled' && (
        <Card>
          <CardHeader><CardTitle>{t('swarm.pins')}</CardTitle></CardHeader>
          <CardContent><PinsPanel deviceId={device.deviceId} editable={isAdmin} /></CardContent>
        </Card>
      )}
    </div>
  )
}
