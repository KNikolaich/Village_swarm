import { useEffect, useRef, useState } from 'react'
import { t } from '@/i18n'
import { formatFixed, formatTick, formatWhen } from '@/lib/format'
import { pickAt } from '@/lib/chartPick'
import { seriesColors } from '@/lib/seriesColors'
import { niceTicks, tickDecimals, timeTicks } from '@/lib/ticks'

export type ChartPoint = { ts: string; avg: number; min: number; max: number }

export type ChartSeries = {
  name?: string
  /** Tailwind classes (written out in full so they are generated): line, marker dot, legend swatch. */
  color?: { stroke: string; fill: string; bg: string; band?: string }
  dashed?: boolean
  points: ChartPoint[]
}

/** Plot area margins in pixels; charts and the shared time axis use the same ones so their marks line up. */
const PLOT = { left: 52, right: 10 }
const H = 150
const TOP = 8
const BOTTOM = 8

/** Width of an element in pixels, kept up to date while the window or layout changes. */
function useWidth() {
  const ref = useRef<HTMLDivElement>(null)
  const [width, setWidth] = useState(600)
  useEffect(() => {
    const el = ref.current
    if (!el) return
    setWidth(el.clientWidth)
    const observer = new ResizeObserver(() => setWidth(el.clientWidth))
    observer.observe(el)
    return () => observer.disconnect()
  }, [])
  return [ref, width] as const
}

type Format = (value: number) => string
type AxisFormat = (value: number, decimals: number) => string

/** One line with its min-max band: the chart most pages need. */
export function Chart({ points, from, to, format, axisFormat }: {
  points: ChartPoint[]
  from: Date
  to: Date
  format: Format
  axisFormat?: AxisFormat
}) {
  return <MultiChart series={[{ points }]} from={from} to={to} format={format} axisFormat={axisFormat} />
}

/**
 * A line chart in real pixels: the mean of each series as a line (a soft min-max band when there is only one),
 * round value marks on the left with thin horizontal lines, and thin vertical lines at round times (labelled once
 * by <TimeAxis>). With several series the readout above lists each at the time under the pointer.
 */
export function MultiChart({ series, from, to, format, axisFormat }: {
  series: ChartSeries[]
  from: Date
  to: Date
  /** Readout above the chart (the value under the pointer). */
  format: Format
  /** Label of a value mark; `decimals` is what the mark spacing needs. Defaults to a plain number. */
  axisFormat?: AxisFormat
}) {
  // The measured container always exists, so its width is known when data arrives after the first render.
  const [ref, width] = useWidth()
  const filled = series.filter((s) => s.points.length > 0)
  return (
    <div ref={ref}>
      {filled.length === 0
        ? <p className="py-6 text-center text-sm text-neutral-500">{t('device.noData')}</p>
        : <ChartBody series={filled} from={from} to={to} width={width} format={format} axisFormat={axisFormat} />}
    </div>
  )
}

function ChartBody({ series, from, to, width, format, axisFormat }: {
  series: ChartSeries[]
  from: Date
  to: Date
  width: number
  format: Format
  axisFormat?: AxisFormat
}) {
  const [hoverMs, setHoverMs] = useState<number | null>(null)
  const all = series.flatMap((s) => s.points)
  const x0 = from.getTime()
  const span = Math.max(1, to.getTime() - x0)
  const plotW = Math.max(1, width - PLOT.left - PLOT.right)
  const { ticks, min, max, step } = niceTicks(Math.min(...all.map((p) => p.min)), Math.max(...all.map((p) => p.max)))
  const x = (ms: number) => PLOT.left + ((ms - x0) / span) * plotW
  const y = (v: number) => TOP + (1 - (v - min) / (max - min)) * (H - TOP - BOTTOM)
  const decimals = tickDecimals(step)
  const label = axisFormat ?? ((v, d) => formatFixed(v, d))
  const vertical = timeTicks(from, to).ticks
  const single = series.length === 1
  // A lone named line (one hornet on a zone chart) still says whose it is, in its own colour.
  const legend = !single || series[0].name != null

  function onMove(e: React.PointerEvent<SVGSVGElement>) {
    // The svg is drawn at its real pixel size, so the pointer position is directly a plot coordinate.
    const at = e.clientX - e.currentTarget.getBoundingClientRect().left
    setHoverMs(x0 + ((at - PLOT.left) / plotW) * span)
  }

  // What the readout and the marker show: per series, the point under the pointer (or the newest one). A series that has no
  // reading there (a sensor that went quiet) shows a dash; the time shown is the pointer's own, not that of one series.
  const picks = series.map((s) => pickAt(s.points, hoverMs))
  const newest = Math.max(...series.map((s) => new Date(s.points[s.points.length - 1].ts).getTime()))
  // One line: the time of the very point shown. Several: the pointer's time, which every line is read at.
  const shownAt = hoverMs == null ? new Date(newest).toISOString()
    : single && picks[0] ? picks[0].ts
      : new Date(Math.min(Math.max(hoverMs, x0), to.getTime())).toISOString()
  const markerMs = hoverMs == null ? null : Math.min(Math.max(hoverMs, x0), to.getTime())

  return (
    <div>
      {!legend ? (
        <p className="text-right text-xs text-neutral-500">
          <span className="tabular-nums text-neutral-200">{picks[0] ? format(picks[0].avg) : '—'}</span> · {formatWhen(shownAt)}
        </p>
      ) : (
        <div className="flex flex-wrap justify-end gap-x-3 gap-y-0.5 text-xs text-neutral-500">
          {series.map((s, i) => (
            <span key={s.name ?? i} className="flex items-center gap-1">
              <span className={`size-2 rounded-full ${(s.color ?? seriesColors[i % seriesColors.length]).bg}`} />
              {s.name} <span className="tabular-nums text-neutral-200">{picks[i] ? format(picks[i].avg) : '—'}</span>
            </span>
          ))}
          <span className="tabular-nums">{formatWhen(shownAt)}</span>
        </div>
      )}
      <svg width={width} height={H} viewBox={`0 0 ${width} ${H}`} className="mt-1 block touch-pan-y" role="img" onPointerMove={onMove} onPointerLeave={() => setHoverMs(null)}>
        {vertical.map((d) => (
          <line key={d.getTime()} x1={x(d.getTime())} x2={x(d.getTime())} y1={TOP} y2={H - BOTTOM} className="stroke-white/10" />
        ))}
        {ticks.map((v) => (
          <g key={v}>
            <line x1={PLOT.left} x2={width - PLOT.right} y1={y(v)} y2={y(v)} className="stroke-white/10" />
            <text x={PLOT.left - 6} y={y(v)} textAnchor="end" dominantBaseline="central" className="fill-neutral-500 text-[10px] tabular-nums">
              {label(v, decimals)}
            </text>
          </g>
        ))}
        {series.map((s, i) => {
          const color = s.color ?? seriesColors[i % seriesColors.length]
          const times = s.points.map((p) => new Date(p.ts).getTime())
          const line = s.points.map((p, k) => `${k === 0 ? 'M' : 'L'}${x(times[k]).toFixed(1)},${y(p.avg).toFixed(1)}`).join(' ')
          const band = [
            ...s.points.map((p, k) => `${x(times[k]).toFixed(1)},${y(p.max).toFixed(1)}`),
            ...s.points.map((_, k) => `${x(times[s.points.length - 1 - k]).toFixed(1)},${y(s.points[s.points.length - 1 - k].min).toFixed(1)}`),
          ].join(' ')
          return (
            <g key={s.name ?? i}>
              {single && <polygon points={band} className={color.band ?? 'fill-amber-400/15'} />}
              <path d={line} fill="none" className={color.stroke} strokeWidth="2" strokeLinejoin="round" strokeDasharray={s.dashed ? '4 3' : undefined} />
              {s.points.length === 1 && <circle cx={x(times[0])} cy={y(s.points[0].avg)} r="4" className={color.fill} />}
              {markerMs != null && picks[i] && <circle cx={x(new Date(picks[i].ts).getTime())} cy={y(picks[i].avg)} r="4" className={color.fill} />}
            </g>
          )
        })}
        {markerMs != null && <line x1={x(markerMs)} x2={x(markerMs)} y1={TOP} y2={H - BOTTOM} className="stroke-neutral-400" strokeDasharray="3 3" />}
      </svg>
    </div>
  )
}

/** One row of time labels for all the charts above it (same plot margins, same marks as their vertical lines). */
export function TimeAxis({ from, to }: { from: Date; to: Date }) {
  const [ref, width] = useWidth()
  const { ticks, stepMs } = timeTicks(from, to)
  const plotW = Math.max(1, width - PLOT.left - PLOT.right)
  const x = (ms: number) => PLOT.left + ((ms - from.getTime()) / Math.max(1, to.getTime() - from.getTime())) * plotW
  return (
    <div ref={ref}>
      <svg width={width} height={18} viewBox={`0 0 ${width} 18`} className="block" aria-hidden>
        {ticks.map((d) => (
          <text key={d.getTime()} x={x(d.getTime())} y={12} textAnchor="middle" className="fill-neutral-500 text-[10px] tabular-nums">
            {formatTick(d, stepMs)}
          </text>
        ))}
      </svg>
    </div>
  )
}
