import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Activity, Square } from 'lucide-react'
import { useState } from 'react'
import { toast } from 'sonner'
import { api, call } from '@/api/client'
import { Button } from '@/components/ui/button'
import { Badge, Select } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { formatTime } from '@/lib/format'
import { monitorHours, panelEveryMin } from '@/lib/monitoring'

const hoursLabel = (h: number) => (h < 1 ? t('monitoring.minutes').replace('{n}', String(h * 60)) : t('device.rangeHours').replace('{n}', String(h)))

/**
 * Monitoring of a panel far away: for a while it reads its screen every few minutes instead of at its own interval (the
 * hive tells it with every read), then goes back to its interval by itself.
 */
export function PanelMonitoring({ deviceId, canControl }: { deviceId: string; canControl: boolean }) {
  const qc = useQueryClient()
  const key = ['display', deviceId]
  const display = useQuery({ queryKey: key, queryFn: () => call(api.GET('/api/devices/{deviceId}/display', { params: { path: { deviceId } } })) })
  const monitor = display.data?.monitor ?? null
  const [hours, setHours] = useState(2)
  const [everyMin, setEveryMin] = useState(monitor?.everyMin ?? 1)
  const [busy, setBusy] = useState(false)

  async function start() {
    setBusy(true)
    try {
      const { response } = await api.PUT('/api/devices/{deviceId}/display/monitoring', { params: { path: { deviceId } }, body: { hours, everyMin } })
      if (!response.ok) {
        toast.error(t('errors.generic'))
        return
      }
      toast.success(monitor ? t('monitoring.changed') : t('monitoring.started'))
      await qc.invalidateQueries({ queryKey: key })
    } finally {
      setBusy(false)
    }
  }

  async function stop() {
    const { response } = await api.DELETE('/api/devices/{deviceId}/display/monitoring', { params: { path: { deviceId } } })
    if (!response.ok && response.status !== 404) {
      toast.error(t('errors.generic'))
      return
    }
    toast.success(t('panelMonitoring.stopped'))
    await qc.invalidateQueries({ queryKey: key })
  }

  return (
    <div className="space-y-3 text-sm">
      {monitor ? (
        <div className="flex flex-wrap items-center gap-2">
          <Badge tone="info"><Activity className="size-3" />{t('monitoring.on')}</Badge>
          <span>
            {t('monitoring.until').replace('{time}', formatTime(monitor.until))} · {t('panelMonitoring.every').replace('{n}', String(monitor.everyMin))}
          </span>
        </div>
      ) : (
        <p className="text-xs text-neutral-500">{t('panelMonitoring.off')}</p>
      )}
      {canControl && (
        <div className="flex flex-wrap items-end gap-3">
          <label className="space-y-1">
            <span className="block text-xs text-neutral-400">{t('monitoring.duration')}</span>
            <Select value={hours} onChange={(e) => setHours(Number(e.target.value))}>
              {monitorHours.map((h) => <option key={h} value={h}>{hoursLabel(h)}</option>)}
            </Select>
          </label>
          <label className="space-y-1">
            <span className="block text-xs text-neutral-400">{t('monitoring.interval')}</span>
            <Select value={everyMin} onChange={(e) => setEveryMin(Number(e.target.value))}>
              {panelEveryMin.map((m) => <option key={m} value={m}>{t('panelMonitoring.every').replace('{n}', String(m))}</option>)}
            </Select>
          </label>
          <Button disabled={busy} onClick={() => void start()}><Activity />{monitor ? t('monitoring.apply') : t('monitoring.start')}</Button>
          {monitor && <Button variant="outline" onClick={() => void stop()}><Square />{t('monitoring.stop')}</Button>}
        </div>
      )}
    </div>
  )
}
