export type SeriesPoint = { ts: string; avg: number; min: number; max: number }

/** Live readings merged into a bucket (by bucket): how many, for a running mean until the next fetch brings the real one. */
const liveCounts = new Map<string, number>()
const BIN_ORIGIN = Date.UTC(2000, 0, 1) // the server's date_bin origin

/**
 * A reading pushed over SignalR goes into every chart of its device and metric at once: into the newest bucket when
 * it falls inside it, else as a new bucket. The chart stays live between its fetches (every 5 minutes).
 */
export function addLiveReading(points: SeriesPoint[], ts: string, value: number, key: string): SeriesPoint[] {
  const ms = new Date(ts).getTime()
  const last = points.at(-1)
  if (!last) return [{ ts, avg: value, min: value, max: value }]
  const lastMs = new Date(last.ts).getTime()
  if (ms < lastMs) return points
  // Bucket width: the smallest step among the newest buckets (a gap in the data is wider, never narrower).
  let width = Infinity
  for (let i = Math.max(1, points.length - 6); i < points.length; i++)
    width = Math.min(width, new Date(points[i].ts).getTime() - new Date(points[i - 1].ts).getTime())
  if (!Number.isFinite(width) || width <= 0) width = 60_000
  if (ms < lastMs + width) {
    const countKey = `${key}|${last.ts}`
    const n = liveCounts.get(countKey) ?? 1
    liveCounts.set(countKey, n + 1)
    const merged = { ts: last.ts, avg: last.avg + (value - last.avg) / (n + 1), min: Math.min(last.min, value), max: Math.max(last.max, value) }
    return [...points.slice(0, -1), merged]
  }
  const start = BIN_ORIGIN + Math.floor((ms - BIN_ORIGIN) / width) * width
  return [...points, { ts: new Date(start).toISOString(), avg: value, min: value, max: value }]
}
