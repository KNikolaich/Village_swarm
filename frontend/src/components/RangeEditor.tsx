import { useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { toast } from 'sonner'
import { api } from '@/api/client'
import { useRanges } from '@/api/queries'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/primitives'
import { metricLabel, t } from '@/i18n'
import { rangeKey } from '@/lib/valueRange'

/** One reading's normal range: below it the number turns light blue, above it pink, inside it stays white. */
function RangeRow({ deviceId, metric, editable }: { deviceId: string; metric: string; editable: boolean }) {
  const qc = useQueryClient()
  const ranges = useRanges()
  const saved = ranges.data?.get(rangeKey(deviceId, metric))
  const [min, setMin] = useState('')
  const [max, setMax] = useState('')
  useEffect(() => {
    setMin(saved?.min != null ? String(saved.min) : '')
    setMax(saved?.max != null ? String(saved.max) : '')
  }, [saved?.min, saved?.max])

  const lo = min.trim() === '' ? null : Number(min.replace(',', '.'))
  const hi = max.trim() === '' ? null : Number(max.replace(',', '.'))
  const numbers = (lo === null || Number.isFinite(lo)) && (hi === null || Number.isFinite(hi))
  const ordered = lo === null || hi === null || lo < hi
  const dirty = (saved?.min ?? null) !== lo || (saved?.max ?? null) !== hi
  const empty = lo === null && hi === null

  async function save() {
    const path = { params: { path: { deviceId, metric } } }
    const { response } = empty ? await api.DELETE('/api/ranges/{deviceId}/{metric}', path) : await api.PUT('/api/ranges/{deviceId}/{metric}', { ...path, body: { min: lo, max: hi } })
    if (!response.ok) {
      toast.error(t('errors.generic'))
      return
    }
    toast.success(t('range.saved'))
    await qc.invalidateQueries({ queryKey: ['ranges'] })
  }

  return (
    <div className="flex flex-wrap items-end gap-2">
      <span className="w-40 text-sm text-neutral-300">{metricLabel(metric)}</span>
      <label className="space-y-1">
        <span className="block text-xs text-sky-300">{t('range.from')}</span>
        <Input className="h-8 w-24" inputMode="decimal" value={min} disabled={!editable} onChange={(e) => setMin(e.target.value)} />
      </label>
      <label className="space-y-1">
        <span className="block text-xs text-pink-300">{t('range.to')}</span>
        <Input className="h-8 w-24" inputMode="decimal" value={max} disabled={!editable} onChange={(e) => setMax(e.target.value)} />
      </label>
      <Button size="sm" disabled={!editable || !dirty || !numbers || !ordered} onClick={() => void save()}>{t('common.save')}</Button>
      {!ordered && <span className="text-xs text-amber-300">{t('range.order')}</span>}
    </div>
  )
}

/** The normal ranges of a device's readings: light blue below, pink above, white inside (empty: no colouring). */
export function RangeEditor({ deviceId, metrics, editable }: { deviceId: string; metrics: readonly string[]; editable: boolean }) {
  if (metrics.length === 0) return null
  return (
    <div className="mt-4 space-y-2 border-t border-neutral-800 pt-3">
      <p className="text-xs text-neutral-500">{t('range.hint')}</p>
      {metrics.map((m) => <RangeRow key={m} deviceId={deviceId} metric={m} editable={editable} />)}
    </div>
  )
}
