/** Round values for a chart axis: steps of 1, 2 or 5 times a power of ten, covering [lo, hi]. */
export function niceTicks(lo: number, hi: number, target = 4): { ticks: number[]; min: number; max: number; step: number } {
  if (hi <= lo) {
    const pad = Math.abs(lo) * 0.1 || 1
    lo -= pad
    hi += pad
  }
  const raw = (hi - lo) / target
  const magnitude = 10 ** Math.floor(Math.log10(raw))
  const norm = raw / magnitude
  const step = (norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 5 ? 5 : 10) * magnitude
  const min = Math.floor(lo / step) * step
  const max = Math.ceil(hi / step) * step
  const count = Math.round((max - min) / step)
  const ticks = Array.from({ length: count + 1 }, (_, i) => Number((min + i * step).toFixed(10)))
  return { ticks, min: ticks[0], max: ticks[ticks.length - 1], step }
}

const MINUTE = 60_000
const HOUR = 60 * MINUTE
const DAY = 24 * HOUR
const timeSteps = [10 * MINUTE, 15 * MINUTE, 30 * MINUTE, HOUR, 2 * HOUR, 3 * HOUR, 6 * HOUR, 12 * HOUR, DAY, 2 * DAY, 7 * DAY, 14 * DAY]

/** Time marks at round local times (whole hours, midnights...) across a window, at most about six of them. */
export function timeTicks(from: Date, to: Date): { ticks: Date[]; stepMs: number } {
  const span = to.getTime() - from.getTime()
  const stepMs = timeSteps.find((s) => span / s <= 6) ?? timeSteps[timeSteps.length - 1]
  // Align to local time: getTimezoneOffset is minutes of UTC minus local.
  const offset = -from.getTimezoneOffset() * MINUTE
  const first = Math.ceil((from.getTime() + offset) / stepMs) * stepMs - offset
  const ticks: Date[] = []
  for (let t = first; t <= to.getTime(); t += stepMs) ticks.push(new Date(t))
  return { ticks, stepMs }
}

/** Decimals worth showing for ticks that are `step` apart (0.2 -> 1, 0.05 -> 2, 5 -> 0). */
export const tickDecimals = (step: number) => Math.min(4, Math.max(0, -Math.floor(Math.log10(step) + 1e-9)))
