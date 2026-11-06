import { useEffect, useState } from 'react'

/**
 * The overview as widgets (backend DashboardLayout): one arrangement for a wide screen, one for a phone. A widget has
 * an id (guard, zone:3, cam:guard-hall...), a width in twelfths of a row (the phone shows one column) and may be hidden.
 */
export const screens = ['desktop', 'phone'] as const
export type Screen = (typeof screens)[number]

export const widths = [3, 4, 6, 8, 12] as const

export interface Widget {
  id: string
  w: number
  hidden: boolean
}

/** What the overview can show right now, in the default order with the default width. */
export interface Available {
  id: string
  w: number
}

/** Where a screen counts as wide: the desktop layout from here on (a landscape tablet too). */
export const wideQuery = '(min-width: 1024px)'

/**
 * The saved arrangement over what exists now: widgets that are gone (a removed zone or camera) drop out, new ones
 * take their default place after the widget that comes before them by default. Nothing saved: the default order.
 */
export function arrange(saved: readonly { id: string; w?: number; hidden?: boolean }[] | null | undefined, available: readonly Available[]): Widget[] {
  if (!saved) return available.map((a) => ({ id: a.id, w: a.w, hidden: false }))
  const exists = new Set(available.map((a) => a.id))
  const result: Widget[] = saved.filter((s) => exists.has(s.id)).map((s) => ({ id: s.id, w: s.w ?? 12, hidden: s.hidden ?? false }))
  available.forEach((a, i) => {
    if (result.some((r) => r.id === a.id)) return
    const before = available.slice(0, i).map((x) => x.id)
    let at = 0
    result.forEach((r, k) => { if (before.includes(r.id)) at = k + 1 })
    result.splice(at, 0, { id: a.id, w: a.w, hidden: false })
  })
  return result
}

/** The list with one widget moved to another one's place. */
export function moveTo(list: readonly Widget[], id: string, target: string): Widget[] {
  const from = list.findIndex((w) => w.id === id)
  const to = list.findIndex((w) => w.id === target)
  if (from < 0 || to < 0 || from === to) return [...list]
  const next = [...list]
  const [item] = next.splice(from, 1)
  next.splice(to, 0, item)
  return next
}

/** One step up (-1) or down (+1). */
export function moveBy(list: readonly Widget[], id: string, step: -1 | 1): Widget[] {
  const i = list.findIndex((w) => w.id === id)
  const j = i + step
  if (i < 0 || j < 0 || j >= list.length) return [...list]
  return moveTo(list, id, list[j].id)
}

/** Whether the window is wide, kept up to date when it is resized or a tablet turns. */
export function useWideScreen(): boolean {
  const [wide, setWide] = useState(() => window.matchMedia(wideQuery).matches)
  useEffect(() => {
    const query = window.matchMedia(wideQuery)
    const onChange = () => setWide(query.matches)
    query.addEventListener('change', onChange)
    return () => query.removeEventListener('change', onChange)
  }, [])
  return wide
}
