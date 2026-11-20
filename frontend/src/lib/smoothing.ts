import { splitSegments } from '@/lib/hornetChart'

/** How a line is smoothed: a moving average, an exponential one, a median (kills single spikes) or a triangular-weighted one. */
export type SmoothKind = 'sma' | 'ema' | 'median' | 'wma'

export const smoothKinds: readonly SmoothKind[] = ['sma', 'ema', 'median', 'wma']

/** A saved way of smoothing: `n` is the window in readings (for EMA the "period": alpha = 2 / (n + 1)). */
export interface SmoothTemplate {
  id: string
  kind: SmoothKind
  n: number
}

export interface SmoothState {
  templates: SmoothTemplate[]
  /** The template in use; null when there is none. */
  selected: string | null
  /** The tick box: smoothing is applied only while it is on. */
  enabled: boolean
}

export const maxWindow = 99

export const defaultState: SmoothState = {
  templates: [
    { id: 'sma5', kind: 'sma', n: 5 },
    { id: 'ema8', kind: 'ema', n: 8 },
    { id: 'median5', kind: 'median', n: 5 },
    { id: 'wma7', kind: 'wma', n: 7 },
  ],
  selected: 'sma5',
  enabled: false,
}

const names: Record<SmoothKind, string> = { sma: 'SMA', ema: 'EMA', median: 'Median', wma: 'WMA' }

export const templateName = (tpl: Pick<SmoothTemplate, 'kind' | 'n'>) => `${names[tpl.kind]}(${tpl.n})`

const clampWindow = (n: number) => Math.min(maxWindow, Math.max(1, Math.round(Number.isFinite(n) ? n : 1)))

/** The window around index i: centred, so the smoothed line is not shifted in time (shorter at the ends). */
function around(values: readonly number[], i: number, n: number): number[] {
  const lo = Math.max(0, i - Math.floor((n - 1) / 2))
  const hi = Math.min(values.length - 1, i + Math.floor(n / 2))
  return values.slice(lo, hi + 1)
}

/** Smooths a run of values; the result has the same length. A window of 1 changes nothing. */
export function smoothValues(values: readonly number[], kind: SmoothKind, window: number): number[] {
  const n = clampWindow(window)
  if (n === 1 || values.length < 2) return [...values]
  switch (kind) {
    case 'ema': {
      const alpha = 2 / (n + 1)
      const out: number[] = [values[0]]
      for (let i = 1; i < values.length; i++) out.push(alpha * values[i] + (1 - alpha) * out[i - 1])
      return out
    }
    case 'median':
      return values.map((_, i) => {
        const w = around(values, i, n).sort((a, b) => a - b)
        const mid = w.length >> 1
        return w.length % 2 ? w[mid] : (w[mid - 1] + w[mid]) / 2
      })
    case 'wma':
      return values.map((_, i) => {
        const lo = Math.max(0, i - Math.floor((n - 1) / 2))
        const centre = i
        let sum = 0
        let weights = 0
        around(values, i, n).forEach((v, k) => {
          const w = n / 2 + 1 - Math.abs(lo + k - centre) // triangle: the middle counts most
          sum += v * w
          weights += w
        })
        return sum / weights
      })
    default:
      return values.map((_, i) => {
        const w = around(values, i, n)
        return w.reduce((a, b) => a + b, 0) / w.length
      })
  }
}

interface Reading {
  ts: string
  avg: number
  min: number
  max: number
}

/**
 * The points of a chart smoothed: the mean and its min-max band alike. Each stretch of readings is smoothed on its own,
 * so a gap (a silent sensor) is not bridged by the filter. Null template: the points as they are.
 */
export function smoothPoints<T extends Reading>(points: readonly T[], tpl: Pick<SmoothTemplate, 'kind' | 'n'> | null): T[] {
  if (!tpl || tpl.n <= 1 || points.length < 2) return points as T[]
  return splitSegments(points).flatMap((seg) => {
    const avg = smoothValues(seg.map((p) => p.avg), tpl.kind, tpl.n)
    const min = smoothValues(seg.map((p) => p.min), tpl.kind, tpl.n)
    const max = smoothValues(seg.map((p) => p.max), tpl.kind, tpl.n)
    return seg.map((p, i) => ({ ...p, avg: avg[i], min: Math.min(min[i], avg[i]), max: Math.max(max[i], avg[i]) }))
  })
}

/** The template to apply now, or null (switched off, or the chosen template was deleted). */
export function activeTemplate(state: SmoothState): SmoothTemplate | null {
  return state.enabled ? state.templates.find((t) => t.id === state.selected) ?? null : null
}

let counter = 0
export const newId = () => `t${Date.now().toString(36)}${(counter++).toString(36)}`

export function addTemplate(state: SmoothState, kind: SmoothKind = 'sma', n = 5): SmoothState {
  const tpl = { id: newId(), kind, n: clampWindow(n) }
  return { ...state, templates: [...state.templates, tpl], selected: tpl.id }
}

export function updateTemplate(state: SmoothState, id: string, patch: Partial<Pick<SmoothTemplate, 'kind' | 'n'>>): SmoothState {
  return {
    ...state,
    templates: state.templates.map((t) => t.id === id ? { ...t, ...patch, n: clampWindow(patch.n ?? t.n) } : t),
  }
}

export function removeTemplate(state: SmoothState, id: string): SmoothState {
  const templates = state.templates.filter((t) => t.id !== id)
  return { ...state, templates, selected: state.selected === id ? templates[0]?.id ?? null : state.selected }
}

/** What was saved, made safe: anything malformed falls back to the defaults. */
export function parseState(raw: string | null): SmoothState {
  if (!raw) return defaultState
  try {
    const p = JSON.parse(raw) as Partial<SmoothState>
    if (!Array.isArray(p.templates)) return defaultState
    const templates = p.templates
      .filter((t): t is SmoothTemplate => !!t && typeof t.id === 'string' && smoothKinds.includes(t.kind) && typeof t.n === 'number')
      .map((t) => ({ id: t.id, kind: t.kind, n: clampWindow(t.n) }))
    const selected = templates.some((t) => t.id === p.selected) ? (p.selected as string) : templates[0]?.id ?? null
    return { templates, selected, enabled: p.enabled === true && selected != null }
  } catch {
    return defaultState
  }
}
