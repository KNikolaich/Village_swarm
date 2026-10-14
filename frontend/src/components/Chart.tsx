import { useEffect, useRef, useState } from 'react'
import { t } from '@/i18n'
import { formatFixed, formatTick, formatWhen } from '@/lib/format'
import { niceTicks, tickDecimals, timeTicks } from '@/lib/ticks'

export type ChartPoint = { ts: string; avg: number; min: number; max: number }

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

/**
 * A line chart in real pixels: the mean as a line, the min-max range of each bucket as a soft band, round value marks
 * on the left with thin horizontal lines, and thin vertical lines at round times (labelled once by <TimeAxis>).
 */
export function Chart({ points, from, to, format, axisFormat }: {
  points: ChartPoint[]
  from: Date
  to: Date
  /** Readout above the chart (the value under the pointer). */
  format: (value: number) => string
  /** Label of a value mark; `decimals` is what the mark spacing needs. Defaults to a plain number. */
  axisFormat?: (value: number, decimals: number) => string
}) {
  // The measured container always exists, so its width is known when data arrives after the first render.
  const [ref, width] = useWidth()
  return (
    <div ref={ref}>
      {points.length === 0
        ? <p className="py-6 text-center text-sm text-neutral-500">{t('device.noData')}</p>
        : <ChartBody points={points} from={from} to={to} width={width} format={format} axisFormat={axisFormat} />}
    </div>
  )
}

function ChartBody({ points, from, to, width, format, axisFormat }: {
  points: ChartPoint[]
  from: Date
  to: Date
  width: number
  format: (value: number) => string
  axisFormat?: (value: number, decimals: number) => string
}) {
  const [hover, setHover] = useState<number | null>(null)
  const times = points.map((p) => new Date(p.ts).getTime())
  const x0 = from.getTime()
  const span = Math.max(1, to.getTime() - x0)
  const plotW = Math.max(1, width - PLOT.left - PLOT.right)
  const { ticks, min, max, step } = niceTicks(Math.min(...points.map((p) => p.min)), Math.max(...points.map((p) => p.max)))
  const x = (ms: number) => PLOT.left + ((ms - x0) / span) * plotW
  const y = (v: number) => TOP + (1 - (v - min) / (max - min)) * (H - TOP - BOTTOM)
  const decimals = tickDecimals(step)
  const label = axisFormat ?? ((v, d) => formatFixed(v, d))
  const vertical = timeTicks(from, to).ticks

  const line = points.map((p, i) => `${i === 0 ? 'M' : 'L'}${x(times[i]).toFixed(1)},${y(p.avg).toFixed(1)}`).join(' ')
  const band = [
    ...points.map((p, i) => `${x(times[i]).toFixed(1)},${y(p.max).toFixed(1)}`),
    ...points.map((_, i) => `${x(times[points.length - 1 - i]).toFixed(1)},${y(points[points.length - 1 - i].min).toFixed(1)}`),
  ].join(' ')

  function onMove(e: React.PointerEvent<SVGSVGElement>) {
    // The svg is drawn at its real pixel size, so the pointer position is directly a plot coordinate.
    const at = e.clientX - e.currentTarget.getBoundingClientRect().left
    let best = 0
    for (let i = 1; i < points.length; i++)
      if (Math.abs(x(times[i]) - at) < Math.abs(x(times[best]) - at)) best = i
    setHover(best)
  }

  const shown = hover != null ? points[hover] : points[points.length - 1]
  return (
    <div>
      <p className="text-right text-xs text-neutral-500">
        <span className="tabular-nums text-neutral-200">{format(shown.avg)}</span> · {formatWhen(shown.ts)}
      </p>
      <svg width={width} height={H} viewBox={`0 0 ${width} ${H}`} className="mt-1 block touch-pan-y" role="img" onPointerMove={onMove} onPointerLeave={() => setHover(null)}>
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
        <polygon points={band} className="fill-amber-400/15" />
        <path d={line} fill="none" className="stroke-amber-400" strokeWidth="2" strokeLinejoin="round" />
        {points.length === 1 && <circle cx={x(times[0])} cy={y(points[0].avg)} r="4" className="fill-amber-400" />}
        {hover != null && (
          <>
            <line x1={x(times[hover])} x2={x(times[hover])} y1={TOP} y2={H - BOTTOM} className="stroke-neutral-400" strokeDasharray="3 3" />
            <circle cx={x(times[hover])} cy={y(points[hover].avg)} r="4" className="fill-amber-300" />
          </>
        )}
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
