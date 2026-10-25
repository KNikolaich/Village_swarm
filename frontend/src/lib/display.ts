/** Screen layout of a monitor (contracts/mqtt-topics.md, vs/v1/hive/display/{id}): the same rules as the hive and the firmware. */

export type Align = 'left' | 'center' | 'right'

export interface Screen {
  width: number
  height: number
}

/** Up to this many items the screen lays out itself; placed by hand, more fit. */
export const maxAuto = 4
export const maxPlaced = 8

/** Text height in pixels for a relative size 1-10: size/10 of the screen height, at least 6 px (DisplayText.PixelHeight). */
export function pixelHeight(size: number, screenHeight: number): number {
  const s = Math.min(10, Math.max(1, Math.round(size)))
  return Math.max(6, Math.round((screenHeight * s) / 10))
}

/** Gap the firmware leaves between a label (smallest font, 6 px) and its value. */
export const labelHeight = 8

/** A reading the way the hive writes it for a screen (DisplayText.Format). */
export function screenText(metric: string, value: number | null | undefined): string {
  if (value === null || value === undefined || Number.isNaN(value)) return '--'
  if (metric.startsWith('temperature')) return `${value.toFixed(1)}°`
  if (metric === 'humidity') return `${value.toFixed(0)}%`
  if (metric === 'battery_voltage') return `${value.toFixed(1)}V`
  if (metric === 'pressure') return value.toFixed(0)
  return value.toFixed(1)
}

/** Keeps a point on the screen: x 0..width-1, y 0..height-1. */
export function clampToScreen(x: number, y: number, screen: Screen): { x: number; y: number } {
  return {
    x: Math.min(screen.width - 1, Math.max(0, Math.round(x))),
    y: Math.min(screen.height - 1, Math.max(0, Math.round(y))),
  }
}

/** Where the rows go when the person switches to placing by hand: one under another, as many as fit. */
export function stackedPlaces(count: number, screen: Screen): { x: number; y: number; size: number }[] {
  const step = Math.max(6, Math.floor(screen.height / Math.max(1, count)))
  const size = Math.max(1, Math.min(10, Math.floor((step / screen.height) * 10)))
  return Array.from({ length: count }, (_, i) => ({ x: 0, y: Math.min(screen.height - 1, i * step), size }))
}

/** The SVG text-anchor for an alignment: x is that edge of the text. */
export function textAnchor(align: Align): 'start' | 'middle' | 'end' {
  return align === 'center' ? 'middle' : align === 'right' ? 'end' : 'start'
}
