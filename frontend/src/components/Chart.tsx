import { useState } from 'react'
import { t } from '@/i18n'
import { formatWhen } from '@/lib/format'

export type ChartPoint = { ts: string; avg: number; min: number; max: number }

const W = 600
const H = 140
const PAD = { left: 8, right: 8, top: 10, bottom: 10 }

/** A small line chart: the mean as a line, the min-max range of each bucket as a soft band. Dependency-free SVG. */
export function Chart({ points, from, to, format }: {
  points: ChartPoint[]
  from: Date
  to: Date
  format: (value: number) => string
}) {
  const [hover, setHover] = useState<number | null>(null)
  if (points.length === 0) return <p className="py-6 text-center text-sm text-neutral-500">{t('device.noData')}</p>

  const times = points.map((p) => new Date(p.ts).getTime())
  const x0 = from.getTime()
  const span = Math.max(1, to.getTime() - x0)
  const lo = Math.min(...points.map((p) => p.min))
  const hi = Math.max(...points.map((p) => p.max))
  const range = hi - lo || 1
  const x = (ms: number) => PAD.left + ((ms - x0) / span) * (W - PAD.left - PAD.right)
  const y = (v: number) => PAD.top + (1 - (v - lo) / range) * (H - PAD.top - PAD.bottom)

  const line = points.map((p, i) => `${i === 0 ? 'M' : 'L'}${x(times[i]).toFixed(1)},${y(p.avg).toFixed(1)}`).join(' ')
  const band = [
    ...points.map((p, i) => `${x(times[i]).toFixed(1)},${y(p.max).toFixed(1)}`),
    ...points.map((_, i) => `${x(times[points.length - 1 - i]).toFixed(1)},${y(points[points.length - 1 - i].min).toFixed(1)}`),
  ].join(' ')

  function onMove(e: React.PointerEvent<SVGSVGElement>) {
    const rect = e.currentTarget.getBoundingClientRect()
    const at = ((e.clientX - rect.left) / rect.width) * W
    let best = 0
    for (let i = 1; i < points.length; i++)
      if (Math.abs(x(times[i]) - at) < Math.abs(x(times[best]) - at)) best = i
    setHover(best)
  }

  const shown = hover != null ? points[hover] : points[points.length - 1]
  return (
    <div>
      <div className="flex items-baseline justify-between text-xs text-neutral-500">
        <span>{format(lo)} … {format(hi)}</span>
        <span><span className="tabular-nums text-neutral-200">{format(shown.avg)}</span> · {formatWhen(shown.ts)}</span>
      </div>
      <svg
        viewBox={`0 0 ${W} ${H}`}
        className="mt-1 h-36 w-full touch-none"
        role="img"
        onPointerMove={onMove}
        onPointerLeave={() => setHover(null)}
      >
        <polygon points={band} className="fill-amber-400/15" />
        <path d={line} fill="none" className="stroke-amber-400" strokeWidth="2" strokeLinejoin="round" vectorEffect="non-scaling-stroke" />
        {points.length === 1 && <circle cx={x(times[0])} cy={H / 2} r="4" className="fill-amber-400" />}
        {hover != null && (
          <>
            <line x1={x(times[hover])} x2={x(times[hover])} y1={PAD.top} y2={H - PAD.bottom} className="stroke-neutral-600" strokeDasharray="3 3" vectorEffect="non-scaling-stroke" />
            <circle cx={x(times[hover])} cy={y(points[hover].avg)} r="4" className="fill-amber-300" />
          </>
        )}
      </svg>
    </div>
  )
}
