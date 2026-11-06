/** A reading of a chart series: only the time matters here. */
export interface TimedPoint {
  ts: string
}

const ms = (p: TimedPoint) => new Date(p.ts).getTime()

/** Index of the point nearest to the time `at`. */
export function nearestIndex(points: readonly TimedPoint[], at: number): number {
  let best = 0
  for (let i = 1; i < points.length; i++) if (Math.abs(ms(points[i]) - at) < Math.abs(ms(points[best]) - at)) best = i
  return best
}

/** How far from a point the pointer may be for the point to still count as "what the series says at that time": a few usual gaps between readings. */
export function reach(points: readonly TimedPoint[]): number {
  if (points.length < 2) return 5 * 60_000
  const gaps = points.slice(1).map((p, i) => ms(p) - ms(points[i])).filter((g) => g > 0).sort((a, b) => a - b)
  const usual = gaps.length ? gaps[Math.floor(gaps.length / 2)] : 60_000
  return Math.max(60_000, usual * 2.5)
}

/**
 * The point of a series under the pointer, or null when the series has no reading there: before it began or after it
 * stopped (a sensor that went silent), so the legend shows a dash instead of an old value with the wrong time.
 * Without a pointer (`at` null) the newest point.
 */
export function pickAt<T extends TimedPoint>(points: readonly T[], at: number | null): T | null {
  if (points.length === 0) return null
  if (at == null) return points[points.length - 1]
  const point = points[nearestIndex(points, at)]
  return Math.abs(ms(point) - at) <= reach(points) ? point : null
}
