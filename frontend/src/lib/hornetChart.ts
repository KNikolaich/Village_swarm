import { reach } from '@/lib/chartPick'

const HOUR = 3_600_000

/** One chart view: the window length in hours and its right edge (null: follow the present moment). */
export interface View {
  hours: number
  end: number | null
}

/**
 * Data is fetched in chunks as long as the window, aligned to multiples of that length, so scrolling back
 * reuses the chunks already loaded and only asks for the new ones. One chunk before the visible ones is
 * fetched ahead so a short scroll does not wait.
 */
export function chunkIndexes(fromMs: number, toMs: number, sizeMs: number, ahead = 1): number[] {
  const first = Math.floor(fromMs / sizeMs) - ahead
  const last = Math.floor((toMs - 1) / sizeMs)
  return Array.from({ length: last - first + 1 }, (_, i) => first + i)
}

/** Chunks glued into one series: oldest first, a point on a chunk border (or fetched twice) kept once. */
export function mergeChunks<T extends { ts: string }>(chunks: readonly (readonly T[] | undefined)[]): T[] {
  const byTime = new Map<number, T>()
  for (const chunk of chunks) for (const p of chunk ?? []) byTime.set(new Date(p.ts).getTime(), p)
  return [...byTime.entries()].sort((a, b) => a[0] - b[0]).map(([, p]) => p)
}

/** The line is cut where a sensor was silent for much longer than its usual gap, instead of joining across the hole. */
export function splitSegments<T extends { ts: string }>(points: readonly T[]): T[][] {
  if (points.length === 0) return []
  const limit = reach(points) * 2
  const segments: T[][] = [[points[0]]]
  for (let i = 1; i < points.length; i++) {
    const gap = new Date(points[i].ts).getTime() - new Date(points[i - 1].ts).getTime()
    if (gap > limit) segments.push([points[i]])
    else segments[segments.length - 1].push(points[i])
  }
  return segments
}

/** Right edge after dragging by `deltaMs` (positive: later). Never past the present: that is "follow now" (null). */
export function panEnd(view: View, deltaMs: number, nowMs: number): number | null {
  const next = (view.end ?? nowMs) + deltaMs
  return next >= nowMs ? null : next
}

/** The same window length with the centre kept, when zooming to another length of the ladder. */
export function zoomTo(view: View, hours: number, nowMs: number): View {
  const end = view.end ?? nowMs
  const centre = end - (view.hours * HOUR) / 2
  const next = centre + (hours * HOUR) / 2
  return { hours, end: view.end == null || next >= nowMs ? null : next }
}

/** Same hue for one kind of unit, lighter shades (and a dash on the last) for further lines sharing its axis. */
export function tint(base: string, index: number): { color: string; dashed: boolean } {
  if (index === 0) return { color: base, dashed: false }
  const white = Math.min(70, index * 35)
  return { color: `color-mix(in srgb, ${base}, white ${white}%)`, dashed: index >= 2 }
}

const unitColors: Record<string, string> = {
  '°': '#fbbf24',
  '%': '#38bdf8',
  V: '#34d399',
  dBm: '#a78bfa',
  KB: '#fb7185',
}
const spare = ['#2dd4bf', '#f472b6', '#a3e635', '#fb923c', '#94a3b8']

/** Base colour of an axis: fixed for the usual units (battery is always green...), the next spare one for the rest. */
export function axisColor(unit: string, order: number): string {
  return unitColors[unit] ?? spare[order % spare.length]
}
