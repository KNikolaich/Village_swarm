import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Plus, X } from 'lucide-react'
import { useEffect, useMemo, useState } from 'react'
import { toast } from 'sonner'
import { api, call } from '@/api/client'
import { useDevices, useLatestTelemetry } from '@/api/queries'
import { Button } from '@/components/ui/button'
import { Input, Select } from '@/components/ui/primitives'
import { metricLabel, t } from '@/i18n'

const maxRows = 4

interface Row {
  /** The device whose reading is shown; '' is this hornet. */
  device: string
  metric: string
  label: string
}

/**
 * The screen of a hornet (0.42" OLED): which readings it shows, from this hornet or from another device (a thermometer in
 * the yard, a room, ...). The hive turns the choice into ready texts and sends them to the screen; with no rows the hornet
 * shows its own readings.
 */
export function DisplayPanel({ deviceId, editable }: { deviceId: string; editable: boolean }) {
  const qc = useQueryClient()
  const key = ['display', deviceId]
  const current = useQuery({ queryKey: key, queryFn: () => call(api.GET('/api/devices/{deviceId}/display', { params: { path: { deviceId } } })) })
  const devices = useDevices()
  const latest = useLatestTelemetry()
  const [rows, setRows] = useState<Row[]>([])
  useEffect(() => {
    if (current.data) setRows(current.data.items.map((i) => ({ device: i.device ?? '', metric: i.metric, label: i.label ?? '' })))
  }, [current.data])

  // The readings each device has had lately: a screen can only show what the hive has seen.
  const metricsOf = useMemo(() => {
    const map = new Map<string, string[]>()
    for (const v of latest.data ?? []) map.set(v.deviceId, [...(map.get(v.deviceId) ?? []), v.metric])
    return (id: string) => (map.get(id) ?? []).slice().sort()
  }, [latest.data])
  const nameOf = (id: string) => (devices.data ?? []).find((d) => d.deviceId === id)?.name ?? id
  const sources = (devices.data ?? []).filter((d) => d.deviceId !== deviceId && metricsOf(d.deviceId).length > 0)

  function setRow(i: number, patch: Partial<Row>) {
    setRows((all) => all.map((r, k) => {
      if (k !== i) return r
      const next = { ...r, ...patch }
      // another device: the first reading it has, when the old choice does not exist there
      if (patch.device !== undefined && !metricsOf(patch.device || deviceId).includes(next.metric)) next.metric = metricsOf(patch.device || deviceId)[0] ?? ''
      return next
    }))
  }

  const complete = rows.every((r) => r.metric !== '')

  async function save() {
    const items = rows.map((r) => ({ device: r.device || null, metric: r.metric, label: r.label.trim() || null }))
    const { response } = await api.PUT('/api/devices/{deviceId}/display', { params: { path: { deviceId } }, body: { items } })
    if (!response.ok) {
      toast.error(t('errors.generic'))
      return
    }
    toast.success(t('display.saved'))
    await qc.invalidateQueries({ queryKey: key })
  }

  return (
    <div className="space-y-3 text-sm">
      <p className="text-xs text-neutral-500">{t('display.hint')}</p>
      {rows.length === 0 && <p className="text-xs text-neutral-400">{t('display.own')}</p>}
      {rows.map((row, i) => {
        const metrics = metricsOf(row.device || deviceId)
        return (
          <div key={i} className="grid grid-cols-[1fr_1fr_auto] items-end gap-2 sm:grid-cols-[1fr_1fr_1fr_auto]">
            <label className="space-y-1">
              <span className="text-xs text-neutral-400">{t('display.source')}</span>
              <Select value={row.device} disabled={!editable} onChange={(e) => setRow(i, { device: e.target.value })}>
                <option value="">{t('display.thisHornet')}</option>
                {sources.map((d) => <option key={d.deviceId} value={d.deviceId}>{d.name}</option>)}
                {row.device && !sources.some((d) => d.deviceId === row.device) && <option value={row.device}>{nameOf(row.device)}</option>}
              </Select>
            </label>
            <label className="space-y-1">
              <span className="text-xs text-neutral-400">{t('display.data')}</span>
              <Select value={row.metric} disabled={!editable} onChange={(e) => setRow(i, { metric: e.target.value })}>
                {row.metric && !metrics.includes(row.metric) && <option value={row.metric}>{metricLabel(row.metric)}</option>}
                {metrics.map((m) => <option key={m} value={m}>{metricLabel(m)}</option>)}
              </Select>
            </label>
            <label className="col-span-2 space-y-1 sm:col-span-1">
              <span className="text-xs text-neutral-400">{t('display.label')}</span>
              <Input value={row.label} maxLength={16} disabled={!editable} placeholder={row.device ? nameOf(row.device) : ''} onChange={(e) => setRow(i, { label: e.target.value })} />
            </label>
            <Button variant="ghost" size="icon" className="size-9 shrink-0" disabled={!editable} title={t('display.remove')} aria-label={t('display.remove')}
              onClick={() => setRows((all) => all.filter((_, k) => k !== i))}><X /></Button>
          </div>
        )
      })}
      <div className="flex flex-wrap items-center gap-3">
        <Button variant="outline" disabled={!editable || rows.length >= maxRows}
          onClick={() => setRows((all) => [...all, { device: '', metric: metricsOf(deviceId)[0] ?? '', label: '' }])}><Plus />{t('display.add')}</Button>
        <Button disabled={!editable || !complete} onClick={() => void save()}>{t('display.save')}</Button>
      </div>
      <p className="text-xs text-neutral-500">{t('display.layout')}</p>
    </div>
  )
}
