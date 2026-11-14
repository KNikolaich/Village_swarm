import { ChevronLeft, ChevronRight, Maximize2, Minimize2, X, ZoomIn, ZoomOut } from 'lucide-react'
import { useCallback, useEffect, useId, useMemo, useRef, useState } from 'react'
import type { DeviceDto } from '@/api/client'
import { useChunkedSeries } from '@/api/queries'
import { useWidth, type ChartPoint } from '@/components/Chart'
import { Button } from '@/components/ui/button'
import { metricLabel, t } from '@/i18n'
import { pickAt } from '@/lib/chartPick'
import { formatFixed, formatTick, formatValue, formatWhen, metricUnit } from '@/lib/format'
import { axisColor, panEnd, splitSegments, tint, zoomTo, type View } from '@/lib/hornetChart'
import { rangeLabel, ranges } from '@/lib/ranges'
import { niceTicks, tickDecimals, timeTicks } from '@/lib/ticks'
import { cn } from '@/lib/utils'

const HOUR = 3_600_000
const AXIS_W = 46
const TOP = 20
const BOTTOM = 22

type Line = {
  key: string
  name: string
  /** Lines of one unit share an axis. */
  unit: string
  format: (v: number) => string
  points: ChartPoint[]
}

const scalar = (ts: string, v: number): ChartPoint => ({ ts, avg: v, min: v, max: v })

/**
 * Every reading of one hornet on one plot: one axis (and colour) per unit, a legend with checkboxes (a switched-off line
 * is not fetched), drag or arrows to scroll through time, the zoom buttons or Ctrl+wheel to change the window; older data
 * is loaded in pages as the window moves back.
 */
export function HornetChartModal({ device, metrics, onClose }: { device: DeviceDto; metrics: string[]; onClose: () => void }) {
  const [view, setView] = useState<View>({ hours: 24, end: null })
  const [full, setFull] = useState(false)
  // Everything the hornet reports is on at first, except the diagnostics (signal, memory).
  const [off, setOff] = useState<Set<string>>(() => new Set(['h:rssi', 'h:heap']))
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const id = window.setInterval(() => setNow(Date.now()), 60_000)
    return () => window.clearInterval(id)
  }, [])

  const to = view.end ?? now
  const from = to - view.hours * HOUR
  const on = (key: string) => !off.has(key)
  const healthOn = ['h:vbat', 'h:rssi', 'h:heap'].some(on)
  const data = useChunkedSeries(device.deviceId, metrics.filter((m) => on(`m:${m}`)), healthOn, view.hours, from, to)

  const entries = useMemo(() => {
    const list: Omit<Line, 'points'>[] = metrics.map((m) => ({
      key: `m:${m}`, name: metricLabel(m), unit: metricUnit(m).trim(), format: (v) => formatValue(m, v),
    }))
    list.push(
      { key: 'h:vbat', name: t('device.battery'), unit: t('units.volt'), format: (v) => formatValue('battery_voltage', v) },
      { key: 'h:rssi', name: t('device.signal'), unit: 'dBm', format: (v) => `${Math.round(v)} dBm` },
      { key: 'h:heap', name: t('device.memory'), unit: 'KB', format: (v) => `${Math.round(v)} KB` },
    )
    return list
  }, [metrics])

  const lines: Line[] = entries.map((e) => {
    const points = e.key.startsWith('m:')
      ? data.metrics[e.key.slice(2)] ?? []
      : data.health.flatMap((r) => {
        const v = e.key === 'h:vbat' ? r.vbat : e.key === 'h:rssi' ? r.rssi : r.heapFree / 1024
        return v == null ? [] : [scalar(r.ts, v)]
      })
    return { ...e, points }
  })

  const toggle = (key: string) => setOff((s) => {
    const next = new Set(s)
    if (!next.delete(key)) next.add(key)
    return next
  })

  const pan = useCallback((deltaMs: number) => setView((v) => ({ ...v, end: panEnd(v, deltaMs, Date.now()) })), [])
  const zoom = useCallback((step: number) => setView((v) => {
    const i = ranges.indexOf(v.hours) + step
    return i < 0 || i >= ranges.length ? v : zoomTo(v, ranges[i], Date.now())
  }), [])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
      else if (e.key === 'ArrowLeft') pan(-view.hours * HOUR / 4)
      else if (e.key === 'ArrowRight') pan(view.hours * HOUR / 4)
    }
    window.addEventListener('keydown', onKey)
    const overflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    return () => {
      window.removeEventListener('keydown', onKey)
      document.body.style.overflow = overflow
    }
  }, [onClose, pan, view.hours])

  const index = ranges.indexOf(view.hours)
  const title = device.name

  return (
    <div role="dialog" aria-modal="true" aria-label={title} className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 p-0 sm:p-4" onClick={onClose}>
      <div
        className={cn('flex flex-col gap-3 overflow-y-auto border border-neutral-800 bg-neutral-950 p-4 shadow-2xl', full ? 'h-dvh w-screen rounded-none' : 'max-h-dvh w-full max-w-5xl rounded-xl')}
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-center gap-1">
          <div className="min-w-0 flex-1">
            <p className="truncate font-semibold">{title}</p>
            <p className="truncate text-xs text-neutral-500">
              {rangeLabel(view.hours)} · {formatWhen(new Date(from).toISOString())} — {formatWhen(new Date(to).toISOString())}
              {data.loading && <span className="ml-2 text-neutral-400">{t('hornetChart.loading')}</span>}
            </p>
          </div>
          <Button variant="ghost" size="icon" title={t('hornetChart.back')} aria-label={t('hornetChart.back')} onClick={() => pan(-view.hours * HOUR / 2)}><ChevronLeft /></Button>
          <Button variant="ghost" size="icon" title={t('hornetChart.forward')} aria-label={t('hornetChart.forward')} disabled={view.end == null} onClick={() => pan(view.hours * HOUR / 2)}><ChevronRight /></Button>
          <Button variant="outline" size="sm" disabled={view.end == null} onClick={() => setView((v) => ({ ...v, end: null }))}>{t('hornetChart.now')}</Button>
          <Button variant="ghost" size="icon" title={t('tileChart.zoomIn')} aria-label={t('tileChart.zoomIn')} disabled={index <= 0} onClick={() => zoom(-1)}><ZoomIn /></Button>
          <Button variant="ghost" size="icon" title={t('tileChart.zoomOut')} aria-label={t('tileChart.zoomOut')} disabled={index < 0 || index >= ranges.length - 1} onClick={() => zoom(1)}><ZoomOut /></Button>
          <Button variant="ghost" size="icon" title={full ? t('tileChart.exitFull') : t('tileChart.full')} aria-label={full ? t('tileChart.exitFull') : t('tileChart.full')} onClick={() => setFull(!full)}>{full ? <Minimize2 /> : <Maximize2 />}</Button>
          <Button variant="ghost" size="icon" title={t('tileChart.close')} aria-label={t('tileChart.close')} onClick={onClose}><X /></Button>
        </div>
        <PlotArea
          lines={lines}
          off={off}
          toggle={toggle}
          from={from}
          to={to}
          height={full ? Math.max(260, window.innerHeight - 240) : 320}
          onPan={pan}
          onZoom={zoom}
        />
        <p className="text-xs text-neutral-500">{t('hornetChart.hint')}</p>
      </div>
    </div>
  )
}

function PlotArea({ lines, off, toggle, from, to, height, onPan, onZoom }: {
  lines: Line[]
  off: Set<string>
  toggle: (key: string) => void
  from: number
  to: number
  height: number
  onPan: (deltaMs: number) => void
  onZoom: (step: number) => void
}) {
  const [ref, width] = useWidth()
  const clip = useId()
  const [hoverMs, setHoverMs] = useState<number | null>(null)
  const drag = useRef<{ x: number; moved: boolean } | null>(null)
  const span = to - from

  // Axes: one per unit among the lines that are on, the first on the left, the next on the right, and so on outwards.
  const shown = lines.filter((l) => !off.has(l.key))
  const units = [...new Set(shown.map((l) => l.unit))]
  const axes = units.map((unit, i) => {
    const mine = shown.filter((l) => l.unit === unit)
    // The scale follows what is in the window, so a close look at a small change fills the plot.
    const visible = mine.flatMap((l) => l.points.filter((p) => { const ms = new Date(p.ts).getTime(); return ms >= from && ms <= to }))
    const scale = visible.length ? niceTicks(Math.min(...visible.map((p) => p.min)), Math.max(...visible.map((p) => p.max))) : null
    return { unit, scale, color: axisColor(unit, i), side: i % 2 === 0 ? 'left' as const : 'right' as const, slot: Math.floor(i / 2) }
  })
  const leftCount = axes.filter((a) => a.side === 'left').length
  const rightCount = axes.length - leftCount
  const plotL = 8 + leftCount * AXIS_W
  const plotR = width - 8 - rightCount * AXIS_W
  const plotW = Math.max(1, plotR - plotL)
  const x = (ms: number) => plotL + ((ms - from) / span) * plotW
  const yOf = (scale: { min: number; max: number }, v: number) => TOP + (1 - (v - scale.min) / (scale.max - scale.min)) * (height - TOP - BOTTOM)
  const { ticks: vertical, stepMs } = timeTicks(new Date(from), new Date(to))
  const firstScale = axes.find((a) => a.scale)?.scale

  // Wheel: Ctrl (or pinch on a trackpad) zooms, a sideways wheel scrolls. Not passive, so the page does not zoom or move.
  const wheel = useRef({ plotW, span, onPan, onZoom })
  wheel.current = { plotW, span, onPan, onZoom }
  useEffect(() => {
    const el = ref.current
    if (!el) return
    const onWheel = (e: WheelEvent) => {
      const w = wheel.current
      if (e.ctrlKey || e.metaKey) {
        e.preventDefault()
        w.onZoom(e.deltaY > 0 ? 1 : -1)
      } else if (Math.abs(e.deltaX) > Math.abs(e.deltaY) || e.shiftKey) {
        e.preventDefault()
        w.onPan(((e.deltaX || e.deltaY) / w.plotW) * w.span)
      }
    }
    el.addEventListener('wheel', onWheel, { passive: false })
    return () => el.removeEventListener('wheel', onWheel)
  }, [ref])

  const atPointer = (e: React.PointerEvent<SVGSVGElement>) => from + ((e.clientX - e.currentTarget.getBoundingClientRect().left - plotL) / plotW) * span
  function onDown(e: React.PointerEvent<SVGSVGElement>) {
    drag.current = { x: e.clientX, moved: false }
    e.currentTarget.setPointerCapture(e.pointerId)
  }
  function onMove(e: React.PointerEvent<SVGSVGElement>) {
    const d = drag.current
    if (d && e.buttons !== 0) {
      const dx = e.clientX - d.x
      if (Math.abs(dx) < 2) return
      d.moved = true
      d.x = e.clientX
      onPan((-dx / plotW) * span)
      setHoverMs(null)
    } else setHoverMs(atPointer(e))
  }
  function onUp(e: React.PointerEvent<SVGSVGElement>) {
    drag.current = null
    if (e.currentTarget.hasPointerCapture(e.pointerId)) e.currentTarget.releasePointerCapture(e.pointerId)
  }

  const markerMs = hoverMs == null ? null : Math.min(Math.max(hoverMs, from), to)
  const picks = new Map(lines.map((l) => [l.key, pickAt(l.points, markerMs ?? to)]))
  const coloured = new Map<string, { color: string; dashed: boolean }>()
  for (const unit of units) {
    const base = axes.find((a) => a.unit === unit)!.color
    shown.filter((l) => l.unit === unit).forEach((l, i) => coloured.set(l.key, tint(base, i)))
  }
  // A line that is off keeps a swatch in the colour it would have.
  for (const l of lines) if (!coloured.has(l.key)) coloured.set(l.key, tint(axisColor(l.unit, units.length), 0))

  return (
    <div ref={ref}>
      <div className="flex flex-wrap gap-x-4 gap-y-1 text-xs">
        {lines.map((l) => {
          const c = coloured.get(l.key)!
          const pick = picks.get(l.key)
          return (
            <label key={l.key} className={cn('flex cursor-pointer select-none items-center gap-1.5', off.has(l.key) ? 'text-neutral-600' : 'text-neutral-300')}>
              <input type="checkbox" checked={!off.has(l.key)} onChange={() => toggle(l.key)} className="size-3.5" style={{ accentColor: c.color }} />
              <span className="inline-block h-0.5 w-4 rounded" style={{ background: c.color }} />
              {l.name}
              {!off.has(l.key) && <span className="tabular-nums text-neutral-200">{pick ? l.format(pick.avg) : '—'}</span>}
            </label>
          )
        })}
        <span className="ml-auto tabular-nums text-neutral-500">{formatWhen(new Date(markerMs ?? to).toISOString())}</span>
      </div>
      {shown.length === 0
        ? <p className="py-16 text-center text-sm text-neutral-500">{t('hornetChart.nothingOn')}</p>
        : (
          <svg
            width={width} height={height} viewBox={`0 0 ${width} ${height}`}
            className="mt-1 block cursor-grab touch-pan-y select-none active:cursor-grabbing" role="img"
            onPointerDown={onDown} onPointerMove={onMove} onPointerUp={onUp} onPointerCancel={onUp} onPointerLeave={() => setHoverMs(null)}
          >
            <defs><clipPath id={clip}><rect x={plotL} y={0} width={plotW} height={height} /></clipPath></defs>
            {vertical.map((d) => (
              <g key={d.getTime()}>
                <line x1={x(d.getTime())} x2={x(d.getTime())} y1={TOP} y2={height - BOTTOM} className="stroke-white/10" />
                <text x={x(d.getTime())} y={height - 6} textAnchor="middle" className="fill-neutral-500 text-[10px] tabular-nums">{formatTick(d, stepMs)}</text>
              </g>
            ))}
            {firstScale && firstScale.ticks.map((v) => (
              <line key={v} x1={plotL} x2={plotR} y1={yOf(firstScale, v)} y2={yOf(firstScale, v)} className="stroke-white/10" />
            ))}
            {axes.map((a) => {
              if (!a.scale) return null
              const ax = a.side === 'left' ? plotL - a.slot * AXIS_W : plotR + a.slot * AXIS_W
              const decimals = tickDecimals(a.scale.step)
              return (
                <g key={a.unit} fill={a.color}>
                  <line x1={ax} x2={ax} y1={TOP} y2={height - BOTTOM} stroke={a.color} strokeOpacity={0.6} />
                  <text x={ax + (a.side === 'left' ? -6 : 6)} y={9} textAnchor={a.side === 'left' ? 'end' : 'start'} className="text-[10px] font-medium">{a.unit}</text>
                  {a.scale.ticks.map((v) => (
                    <text key={v} x={ax + (a.side === 'left' ? -6 : 6)} y={yOf(a.scale!, v)} textAnchor={a.side === 'left' ? 'end' : 'start'} dominantBaseline="central" className="text-[10px] tabular-nums">
                      {formatFixed(v, decimals)}
                    </text>
                  ))}
                </g>
              )
            })}
            <g clipPath={`url(#${clip})`}>
              {shown.map((l) => {
                const axis = axes.find((a) => a.unit === l.unit)!
                if (!axis.scale || l.points.length === 0) return null
                const c = coloured.get(l.key)!
                const scale = axis.scale
                return (
                  <g key={l.key}>
                    {splitSegments(l.points).map((seg, k) => seg.length === 1
                      ? <circle key={k} cx={x(new Date(seg[0].ts).getTime())} cy={yOf(scale, seg[0].avg)} r="2.5" fill={c.color} />
                      : <path key={k} d={seg.map((p, j) => `${j === 0 ? 'M' : 'L'}${x(new Date(p.ts).getTime()).toFixed(1)},${yOf(scale, p.avg).toFixed(1)}`).join(' ')}
                        fill="none" stroke={c.color} strokeWidth="2" strokeLinejoin="round" strokeDasharray={c.dashed ? '4 3' : undefined} />)}
                    {markerMs != null && picks.get(l.key) && (
                      <circle cx={x(new Date(picks.get(l.key)!.ts).getTime())} cy={yOf(scale, picks.get(l.key)!.avg)} r="4" fill={c.color} />
                    )}
                  </g>
                )
              })}
            </g>
            {markerMs != null && <line x1={x(markerMs)} x2={x(markerMs)} y1={TOP} y2={height - BOTTOM} className="stroke-neutral-400" strokeDasharray="3 3" />}
          </svg>
        )}
    </div>
  )
}
