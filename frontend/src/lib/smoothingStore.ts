import { useCallback, useMemo, useSyncExternalStore } from 'react'
import { activeTemplate, defaultState, parseState, smoothPoints, type SmoothState } from '@/lib/smoothing'

const KEY = 'hive.chartSmoothing'
const listeners = new Set<() => void>()
let cache: SmoothState | null = null

function read(): SmoothState {
  if (cache) return cache
  try {
    cache = parseState(globalThis.localStorage?.getItem(KEY) ?? null)
  } catch {
    cache = defaultState
  }
  return cache
}

function write(next: SmoothState) {
  cache = next
  try {
    globalThis.localStorage?.setItem(KEY, JSON.stringify(next))
  } catch {
    // private window or storage full: applies until the page is closed
  }
  listeners.forEach((l) => l())
}

function subscribe(listener: () => void) {
  listeners.add(listener)
  // Another tab changed it.
  const onStorage = (e: StorageEvent) => {
    if (e.key !== KEY) return
    cache = null
    listener()
  }
  window.addEventListener('storage', onStorage)
  return () => {
    listeners.delete(listener)
    window.removeEventListener('storage', onStorage)
  }
}

/** The saved smoothing setting, shared by every chart on the page; changing it redraws them all. */
export function useSmoothingState() {
  const state = useSyncExternalStore(subscribe, read, () => defaultState)
  const update = useCallback((fn: (s: SmoothState) => SmoothState) => write(fn(read())), [])
  return [state, update] as const
}

/** A function that smooths the points of a series with the template in use (identity while smoothing is off). */
export function useSmoother() {
  const [state] = useSmoothingState()
  const tpl = activeTemplate(state)
  return useMemo(() => <T extends { ts: string; avg: number; min: number; max: number }>(points: T[]) => smoothPoints(points, tpl), [tpl])
}
