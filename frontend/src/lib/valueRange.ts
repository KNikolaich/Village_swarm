import type { Schemas } from '@/api/client'

export type ValueRange = Schemas['ValueRangeDto']

export type RangeState = 'low' | 'high' | 'ok'

/** Where a reading stands against the range someone set for it: below, above or inside (also when no range is set). */
export function rangeState(value: number, range?: { min?: number | null; max?: number | null } | null): RangeState {
  if (range?.min != null && value < range.min) return 'low'
  if (range?.max != null && value > range.max) return 'high'
  return 'ok'
}

/** The colour of a number: light blue below the range, pink above it, the usual white inside. */
export const rangeClass = (state: RangeState): string | undefined =>
  state === 'low' ? 'text-sky-300' : state === 'high' ? 'text-pink-300' : undefined

export const rangeKey = (deviceId: string, metric: string) => `${deviceId}/${metric}`

export const rangesByKey = (list: readonly ValueRange[] | undefined) =>
  new Map((list ?? []).map((r) => [rangeKey(r.deviceId, r.metric), r] as const))
