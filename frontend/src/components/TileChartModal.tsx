import { Maximize2, Minimize2, X, ZoomIn, ZoomOut } from 'lucide-react'
import { useEffect, useState } from 'react'
import { Link } from 'react-router'
import type { DeviceDto } from '@/api/client'
import { historyWindow, useMetricSeries } from '@/api/queries'
import { MultiChart, TimeAxis } from '@/components/Chart'
import { Button } from '@/components/ui/button'
import { metricLabel, t } from '@/i18n'
import { formatFixed, formatValue, metricUnit } from '@/lib/format'
import { ranges, rangeLabel } from '@/lib/ranges'
import { cn } from '@/lib/utils'

/** The chart of one tile: the tile's own reading, and the humidity of the same hornet beside a temperature. */
function useTileSeries(device: DeviceDto, metric: string, hours: number, enabled = true) {
  const lines = enabled ? [{ device: device.deviceId, metric }] : []
  const result = useMetricSeries(lines, hours)
  return enabled ? result[0] : undefined
}

/** What the tooltip over a tile shows: the last day of the reading as a small chart. */
export function TileTooltip({ device, metric, title }: { device: DeviceDto; metric: string; title: string }) {
  const hours = 24
  const query = useTileSeries(device, metric, hours)
  const { from, to } = historyWindow(hours)
  return (
    <div className="pointer-events-none w-72 rounded-lg border border-neutral-700 bg-neutral-900 p-3 shadow-xl">
      <p className="mb-1 truncate text-xs text-neutral-400">{title} · {metricLabel(metric)} · {rangeLabel(hours)}</p>
      {query?.isLoading
        ? <p className="py-6 text-center text-sm text-neutral-500">…</p>
        : <MultiChart series={[{ points: query?.data ?? [] }]} from={from} to={to} height={110} format={(v) => formatValue(metric, v)} axisFormat={(v, d) => `${formatFixed(v, d)}${metricUnit(metric)}`} />}
    </div>
  )
}

/** The chart of a reading in a window of its own: zoom (shorter / longer period), full screen, close, go to the hornet. */
export function TileChartModal({ device, metric, title, onClose }: { device: DeviceDto; metric: string; title: string; onClose: () => void }) {
  const [hours, setHours] = useState(24)
  const [full, setFull] = useState(false)
  const query = useTileSeries(device, metric, hours)
  const { from, to } = historyWindow(hours)
  const index = ranges.indexOf(hours)

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
    window.addEventListener('keydown', onKey)
    const overflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    return () => {
      window.removeEventListener('keydown', onKey)
      document.body.style.overflow = overflow
    }
  }, [onClose])

  return (
    <div role="dialog" aria-modal="true" aria-label={title} className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 p-0 sm:p-4" onClick={onClose}>
      <div
        className={cn('flex flex-col gap-3 border border-neutral-800 bg-neutral-950 p-4 shadow-2xl', full ? 'h-dvh w-screen rounded-none' : 'max-h-dvh w-full max-w-3xl rounded-xl')}
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-center gap-1">
          <div className="min-w-0 flex-1">
            <p className="truncate font-semibold">{title}</p>
            <p className="truncate text-xs text-neutral-500">{metricLabel(metric)} · {rangeLabel(hours)}</p>
          </div>
          <Button variant="ghost" size="icon" title={t('tileChart.zoomIn')} aria-label={t('tileChart.zoomIn')} disabled={index <= 0} onClick={() => setHours(ranges[index - 1])}><ZoomIn /></Button>
          <Button variant="ghost" size="icon" title={t('tileChart.zoomOut')} aria-label={t('tileChart.zoomOut')} disabled={index < 0 || index >= ranges.length - 1} onClick={() => setHours(ranges[index + 1])}><ZoomOut /></Button>
          <Button variant="ghost" size="icon" title={full ? t('tileChart.exitFull') : t('tileChart.full')} aria-label={full ? t('tileChart.exitFull') : t('tileChart.full')} onClick={() => setFull(!full)}>{full ? <Minimize2 /> : <Maximize2 />}</Button>
          <Button variant="ghost" size="icon" title={t('tileChart.close')} aria-label={t('tileChart.close')} onClick={onClose}><X /></Button>
        </div>
        {query?.isLoading
          ? <p className="py-16 text-center text-sm text-neutral-500">…</p>
          : <MultiChart series={[{ points: query?.data ?? [] }]} from={from} to={to} height={full ? Math.max(240, window.innerHeight - 220) : 280} format={(v) => formatValue(metric, v)} axisFormat={(v, d) => `${formatFixed(v, d)}${metricUnit(metric)}`} />}
        <TimeAxis from={from} to={to} />
        <div className="flex justify-end">
          <Link to={`/swarm/${encodeURIComponent(device.deviceId)}`} className="inline-flex h-8 items-center rounded-lg border border-neutral-700 px-3 text-xs font-medium text-neutral-200 hover:bg-neutral-800">
            {t('tileChart.openHornet')}
          </Link>
        </div>
      </div>
    </div>
  )
}
