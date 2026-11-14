import type { SVGProps } from 'react'

/** The outline of a gear: `teeth` square-ish teeth between the inner and the outer radius. */
function gearPath(cx: number, cy: number, outer: number, inner: number, teeth: number): string {
  const step = (Math.PI * 2) / teeth
  const points: string[] = []
  for (let i = 0; i < teeth; i++) {
    const a = i * step
    // A tooth takes the middle 45% of its sector; the rest is the root between two teeth.
    for (const [offset, radius] of [[0.2, inner], [0.3, outer], [0.7, outer], [0.8, inner]] as const) {
      const angle = a + offset * step
      points.push(`${(cx + Math.cos(angle) * radius).toFixed(2)} ${(cy + Math.sin(angle) * radius).toFixed(2)}`)
    }
  }
  return `M${points.join('L')}Z`
}

const big = { x: 9, y: 9, outer: 7.2, inner: 5.6, teeth: 8, hole: 2.2 }
const small = { x: 17.5, y: 17.5, outer: 4.6, inner: 3.4, teeth: 6, hole: 1.3 }

/**
 * Two gears: the hive's global settings (one gear is for the small, per-page ones). Drawn like the lucide icons
 * (24x24, currentColor stroke), which have no double gear of their own.
 */
export function GlobalSettingsIcon(props: SVGProps<SVGSVGElement>) {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      width="24"
      height="24"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      {...props}
    >
      <path d={gearPath(big.x, big.y, big.outer, big.inner, big.teeth)} />
      <circle cx={big.x} cy={big.y} r={big.hole} />
      <path d={gearPath(small.x, small.y, small.outer, small.inner, small.teeth)} />
      <circle cx={small.x} cy={small.y} r={small.hole} />
    </svg>
  )
}
