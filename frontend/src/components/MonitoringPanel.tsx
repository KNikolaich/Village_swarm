import { useQueryClient } from '@tanstack/react-query'
import { Activity, Square } from 'lucide-react'
import { useState } from 'react'
import { toast } from 'sonner'
import { api, type DeviceDto, type MonitoringDto } from '@/api/client'
import { keys, useMonitoring } from '@/api/queries'
import { Button } from '@/components/ui/button'
import { Badge, Input, Select } from '@/components/ui/primitives'
import { metricLabel, t } from '@/i18n'
import { formatFixed, formatTime, metricUnit } from '@/lib/format'
import { monitorHours, monitorIntervals } from '@/lib/monitoring'

const hoursLabel = (h: number) => (h < 1 ? t('monitoring.minutes').replace('{n}', String(h * 60)) : t('device.rangeHours').replace('{n}', String(h)))

function left(until: string, now = Date.now()) {
  const min = Math.max(0, Math.round((new Date(until).getTime() - now) / 60_000))
  return min >= 60 ? t('monitoring.leftHours').replace('{h}', String(Math.floor(min / 60))).replace('{m}', String(min % 60)) : t('monitoring.leftMinutes').replace('{m}', String(min))
}

const parseLimit = (s: string) => (s.trim() === '' ? null : Number(s.replace(',', '.')))

/**
 * Monitoring mode on the device page: the thermometer reports every few seconds for a while (a pressure cooker, a
 * smokehouse) and goes back to its usual interval by itself. Optional limits raise an alarm in Telegram when a reading
 * leaves them; silence for three intervals raises one too.
 */
export function MonitoringPanel({ device, metrics, canControl, onStarted }: {
  device: DeviceDto
  /** Temperature metrics the hornet reports; the first is the default. */
  metrics: string[]
  canControl: boolean
  onStarted?: () => void
}) {
  const qc = useQueryClient()
  const monitoring = useMonitoring()
  const session = (monitoring.data ?? []).find((m) => m.deviceId === device.deviceId)
  const [editing, setEditing] = useState(false)

  if (!session && !canControl) return null
  return (
    <div className="space-y-3 text-sm">
      {session ? <Session session={session} /> : <p className="text-xs text-neutral-500">{t('monitoring.hint')}</p>}
      {canControl && (!session || editing) && (
        <MonitoringForm
          key={session?.until ?? 'new'}
          deviceId={device.deviceId}
          metrics={metrics}
          session={session}
          onDone={async (started) => {
            setEditing(false)
            await qc.invalidateQueries({ queryKey: keys.monitoring })
            await qc.invalidateQueries({ queryKey: keys.devices })
            if (started) onStarted?.()
          }}
        />
      )}
      {canControl && session && !editing && (
        <div className="flex flex-wrap gap-2">
          <Button variant="outline" onClick={() => setEditing(true)}>{t('monitoring.change')}</Button>
          <Button
            variant="outline"
            onClick={async () => {
              const { response } = await api.DELETE('/api/telemetry/monitoring/{deviceId}', { params: { path: { deviceId: device.deviceId } } })
              if (!response.ok && response.status !== 404) {
                toast.error(t('errors.generic'))
                return
              }
              toast.success(t('monitoring.stopped'))
              await qc.invalidateQueries({ queryKey: keys.monitoring })
              await qc.invalidateQueries({ queryKey: keys.devices })
            }}
          >
            <Square />{t('monitoring.stop')}
          </Button>
        </div>
      )}
    </div>
  )
}

function Session({ session }: { session: MonitoringDto }) {
  const unit = metricUnit(session.metric)
  const limit = (v: number) => `${formatFixed(v, 1)}${unit}`
  return (
    <div className="space-y-2">
      <div className="flex flex-wrap items-center gap-2">
        <Badge tone="info"><Activity className="size-3" />{t('monitoring.on')}</Badge>
        <span>
          {t('monitoring.until').replace('{time}', formatTime(session.until))} · {t('monitoring.every').replace('{n}', String(session.intervalS))} · {left(session.until)}
        </span>
      </div>
      <p className="text-xs text-neutral-400">
        {metricLabel(session.metric)}
        {session.below != null && ` · ${t('monitoring.alarmBelow')} ${limit(session.below)}`}
        {session.above != null && ` · ${t('monitoring.alarmAbove')} ${limit(session.above)}`}
        {session.below == null && session.above == null && ` · ${t('monitoring.noLimits')}`}
      </p>
      {session.breach && (
        <p className="rounded-md bg-red-500/15 px-3 py-2 text-red-300">
          {session.breach === 'below'
            ? `${t('monitoring.breachBelow')} ${limit(session.below ?? 0)}`
            : `${t('monitoring.breachAbove')} ${limit(session.above ?? 0)}`}
        </p>
      )}
      {session.staleSince && (
        <p className="rounded-md bg-red-500/15 px-3 py-2 text-red-300">{t('monitoring.stale').replace('{time}', formatTime(session.staleSince))}</p>
      )}
    </div>
  )
}

function MonitoringForm({ deviceId, metrics, session, onDone }: {
  deviceId: string
  metrics: string[]
  session?: MonitoringDto
  onDone: (started: boolean) => Promise<void>
}) {
  const [hours, setHours] = useState(2)
  const [intervalS, setIntervalS] = useState(session?.intervalS ?? 10)
  const [metric, setMetric] = useState(session?.metric ?? metrics[0] ?? 'temperature')
  const [below, setBelow] = useState(session?.below != null ? String(session.below) : '')
  const [above, setAbove] = useState(session?.above != null ? String(session.above) : '')
  const [busy, setBusy] = useState(false)
  const lo = parseLimit(below)
  const hi = parseLimit(above)
  const valid = (lo == null || Number.isFinite(lo)) && (hi == null || Number.isFinite(hi)) && (lo == null || hi == null || lo < hi)
  const unit = metricUnit(metric)

  async function start() {
    setBusy(true)
    try {
      const { response, error } = await api.PUT('/api/telemetry/monitoring/{deviceId}', {
        params: { path: { deviceId } },
        body: { hours, intervalS, metric, below: lo, above: hi },
      })
      if (!response.ok) {
        const reason = (error as { errors?: Record<string, string[]> } | undefined)?.errors?.monitoring?.[0]
        toast.error(reason ?? t('errors.generic'))
        return
      }
      toast.success(session ? t('monitoring.changed') : t('monitoring.started'))
      await onDone(!session)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="space-y-3">
      <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
        <label className="space-y-1">
          <span className="block text-xs text-neutral-400">{t('monitoring.duration')}</span>
          <Select value={hours} onChange={(e) => setHours(Number(e.target.value))}>
            {monitorHours.map((h) => <option key={h} value={h}>{hoursLabel(h)}</option>)}
          </Select>
        </label>
        <label className="space-y-1">
          <span className="block text-xs text-neutral-400">{t('monitoring.interval')}</span>
          <Select value={intervalS} onChange={(e) => setIntervalS(Number(e.target.value))}>
            {monitorIntervals.map((s) => <option key={s} value={s}>{t('monitoring.seconds').replace('{n}', String(s))}</option>)}
          </Select>
        </label>
        <label className="space-y-1">
          <span className="block text-xs text-neutral-400">{t('monitoring.alarmBelow')}, {unit}</span>
          <Input inputMode="decimal" placeholder="—" value={below} onChange={(e) => setBelow(e.target.value)} />
        </label>
        <label className="space-y-1">
          <span className="block text-xs text-neutral-400">{t('monitoring.alarmAbove')}, {unit}</span>
          <Input inputMode="decimal" placeholder="—" value={above} onChange={(e) => setAbove(e.target.value)} />
        </label>
      </div>
      {metrics.length > 1 && (
        <label className="block space-y-1">
          <span className="block text-xs text-neutral-400">{t('monitoring.sensor')}</span>
          <Select value={metric} onChange={(e) => setMetric(e.target.value)} className="w-auto">
            {metrics.map((m) => <option key={m} value={m}>{metricLabel(m)}</option>)}
          </Select>
        </label>
      )}
      <p className="text-xs text-neutral-500">{t('monitoring.alarmHint')}</p>
      {!valid && <p className="text-xs text-amber-300">{t('monitoring.limitsBad')}</p>}
      <Button disabled={!valid || busy} onClick={() => void start()}>
        <Activity />{session ? t('monitoring.apply') : t('monitoring.start')}
      </Button>
    </div>
  )
}
