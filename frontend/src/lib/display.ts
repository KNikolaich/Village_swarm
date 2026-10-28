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

/** Pixel heights a font can be set to on a screen the hive draws (DisplayText.MinPx/MaxPx). */
export const minPx = 6
export const maxPx = 120
export const rotations = [0, 90, 180, 270] as const

export interface FontInfo {
  id: string
  name: string
  cyrillic: boolean
  digitsOnly: boolean
  builtin: boolean
  styles: string[]
  /** The sizes in pixels the family reads well at (a pixel font: its grid and whole multiples). */
  sizes: number[]
}

/** The offered size nearest to <px>: a font changes, the text keeps about its height. */
export function nearestSize(px: number, sizes: number[]): number {
  if (sizes.length === 0) return clampPx(px)
  return sizes.reduce((best, s) => (Math.abs(s - px) < Math.abs(best - px) ? s : best), sizes[0])
}

/** The relative size 1-10 a size in pixels stands for (screens that also lay out by size need one). */
export function sizeForPx(px: number, screenHeight: number): number {
  return Math.min(10, Math.max(1, Math.round((px * 10) / screenHeight)))
}

export type ClockKind = 'time' | 'date'

/** How the hive's clock may be written (DisplayText.ClockFormats); the first is the default. */
export const clockFormats: Record<ClockKind, string[]> = {
  time: ['HH:mm', 'H:mm'],
  date: ['d MMMM', 'd MMM', 'dd.MM', 'dd.MM.yyyy', 'dd/MM', 'dddd', 'ddd, d MMM', 'd MMMM yyyy', 'MMMM'],
}

const weekdays = ['понедельник', 'вторник', 'среда', 'четверг', 'пятница', 'суббота', 'воскресенье']
const weekdaysShort = ['пн', 'вт', 'ср', 'чт', 'пт', 'сб', 'вс']
const months = ['январь', 'февраль', 'март', 'апрель', 'май', 'июнь', 'июль', 'август', 'сентябрь', 'октябрь', 'ноябрь', 'декабрь']
const monthsOf = ['января', 'февраля', 'марта', 'апреля', 'мая', 'июня', 'июля', 'августа', 'сентября', 'октября', 'ноября', 'декабря']
const monthsShort = ['янв', 'фев', 'мар', 'апр', 'мая', 'июн', 'июл', 'авг', 'сен', 'окт', 'ноя', 'дек']

/** The clock as the hive writes it (DisplayText.ClockText), for the preview: a month after a day is "of" it. */
export function clockText(format: string, at: Date): string {
  const withDay = format.includes('d')
  const two = (n: number) => String(n).padStart(2, '0')
  const weekday = (at.getDay() + 6) % 7
  return format.replace(/dddd|ddd|dd|d|MMMM|MMM|MM|yyyy|HH|H|mm/g, (token) => {
    switch (token) {
      case 'dddd': return weekdays[weekday]
      case 'ddd': return weekdaysShort[weekday]
      case 'dd': return two(at.getDate())
      case 'd': return String(at.getDate())
      case 'MMMM': return (withDay ? monthsOf : months)[at.getMonth()]
      case 'MMM': return monthsShort[at.getMonth()]
      case 'MM': return two(at.getMonth() + 1)
      case 'yyyy': return String(at.getFullYear())
      case 'HH': return two(at.getHours())
      case 'H': return String(at.getHours())
      default: return two(at.getMinutes())
    }
  })
}

/** The mark the picker shows next to a font: Digi for digits only, En without Russian letters, RU with them. */
export function fontBadge(font: Pick<FontInfo, 'cyrillic' | 'digitsOnly'>): 'Digi' | 'En' | 'RU' {
  return font.digitsOnly ? 'Digi' : font.cyrillic ? 'RU' : 'En'
}

/** Keeps a size in pixels inside what a font can be set to. */
export function clampPx(px: number): number {
  return Math.min(maxPx, Math.max(minPx, Math.round(px) || minPx))
}

/** How the picture behind the items must be prepared: the exact size of the screen as it is turned. */
export function backgroundSize(screen: Screen): string {
  return `${screen.width}×${screen.height}`
}
