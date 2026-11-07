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

/** A state of a device as on (true), off (false) or unknown (null): the same words as the hive's DisplayFeedService.IsOn. */
export function isOn(value: unknown): boolean | null {
  if (typeof value === 'boolean') return value
  if (typeof value === 'number') return value !== 0
  if (typeof value !== 'string') return null
  const v = value.trim().toLowerCase()
  if (['on', 'true', '1', 'open'].includes(v)) return true
  if (['off', 'false', '0', 'closed'].includes(v)) return false
  return null
}

/** The states of a device that are a switch (a relay: on or off): what a screen can show as "вкл" and "выкл" or as two pictures. */
export function switchChannels(state: unknown): string[] {
  if (state === null || typeof state !== 'object') return []
  return Object.entries(state as Record<string, unknown>).filter(([, v]) => isOn(v) !== null).map(([k]) => k).sort()
}

/** What a state item shows in words: the person's own or вкл / выкл; -- while the device has not said. */
export function stateText(on: boolean | null, onText: string, offText: string): string {
  if (on === null) return '--'
  return on ? onText.trim() || 'вкл' : offText.trim() || 'выкл'
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

/** The list with the item at <i> moved one place up (-1) or down (+1); unchanged at either end. */
export function moved<T>(all: T[], i: number, by: -1 | 1): T[] {
  const j = i + by
  if (i < 0 || i >= all.length || j < 0 || j >= all.length) return all
  const next = all.slice()
  ;[next[i], next[j]] = [next[j], next[i]]
  return next
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

// ------------------------------------------------------------------ dragging rows and the battery icon over the preview

/** A box of the page in CSS pixels (getBoundingClientRect), and the screen pixels it shows. */
export interface PageBox {
  left: number
  top: number
  width: number
  height: number
}

/**
 * Where a pointer is on the screen: the exact (not yet rounded) screen point under it. The preview is shown at any size (it may be
 * scaled down to fit a phone) and the screen it shows is already the turned one (a panel on its side is 416 by 240 whatever the person
 * does with the picture), so only the size of the box matters; rotations and inversion change the picture, not these coordinates.
 */
export function pointToScreen(clientX: number, clientY: number, box: PageBox, screen: Screen): { x: number; y: number } {
  return { x: ((clientX - box.left) / box.width) * screen.width, y: ((clientY - box.top) / box.height) * screen.height }
}

/** The anchor range of x for a text <width> points wide, on a screen <screenWidth> wide, so that all of it is on the screen. */
export function anchorRange(align: Align, width: number, screenWidth: number): { min: number; max: number } {
  if (width >= screenWidth) {
    const mid = align === 'center' ? Math.round(screenWidth / 2) : align === 'right' ? screenWidth : 0
    return { min: mid, max: mid }  // wider than the screen: nothing fits, the nearest anchor is the best
  }
  if (align === 'center') return { min: Math.ceil(width / 2), max: Math.floor(screenWidth - width / 2) }
  if (align === 'right') return { min: Math.ceil(width), max: screenWidth }
  return { min: 0, max: Math.floor(screenWidth - width) }
}

const clamp = (v: number, min: number, max: number) => Math.min(max, Math.max(min, v))

/**
 * The (x, y) a row gets when the pointer holds it at <grab> (the offset from its anchor where it was taken): rounded to whole screen points
 * and kept so that the whole text stays on the screen. x is the left edge, the middle or the right edge of the text as <align> says, so
 * the range depends on it: a row aligned right cannot go below its own width. <height> is the text's height.
 */
export function dragRow(pointer: { x: number; y: number }, grab: { dx: number; dy: number }, align: Align, width: number, height: number, screen: Screen): { x: number; y: number } {
  const range = anchorRange(align, width, screen.width)
  return {
    x: clamp(Math.round(pointer.x - grab.dx), range.min, range.max),
    y: clamp(Math.round(pointer.y - grab.dy), 0, Math.max(0, Math.floor(screen.height - height))),
  }
}

/** The left and right edges, and top and bottom, a text of <width> by <height> takes when it is placed at (x, y) as <align> says. */
export function rowExtent(row: { x: number; y: number; align: Align }, width: number, height: number): { left: number; right: number; top: number; bottom: number } {
  const left = row.align === 'center' ? row.x - width / 2 : row.align === 'right' ? row.x - width : row.x
  return { left, right: left + width, top: row.y, bottom: row.y + height }
}

/** Whether some of the text is outside the screen (a little under a point either way is rounding). */
export function outsideScreen(extent: { left: number; right: number; top: number; bottom: number }, screen: Screen): boolean {
  const slack = 1
  return extent.left < -slack || extent.top < -slack || extent.right > screen.width + slack || extent.bottom > screen.height + slack
}

/** The place nearest to this one where the whole text is on the screen. */
export function bringBack(row: { x: number; y: number; align: Align }, width: number, height: number, screen: Screen): { x: number; y: number } {
  const range = anchorRange(row.align, width, screen.width)
  return { x: clamp(row.x, range.min, range.max), y: clamp(row.y, 0, Math.max(0, Math.floor(screen.height - height))) }
}

/** The battery icon of a screen the hive draws (display.battery): its base size, and where it stands when the person did not say. */
export const batteryBase = { width: 10, height: 18, margin: 6, top: 4, maxScale: 4 } as const

export interface BatteryState {
  enabled: boolean
  /** The top left corner; null: the top right corner. */
  x: number | null
  y: number | null
  scale: number
}

/** The box the icon takes on the screen (DrawBatteryCorner: the top right corner at the base size by default). */
export function batteryBox(b: BatteryState, screen: Screen): PageBox {
  const scale = clamp(Math.round(b.scale) || 1, 1, batteryBase.maxScale)
  const width = batteryBase.width * scale
  return {
    left: b.x ?? screen.width - batteryBase.margin * scale - width,
    top: b.y ?? batteryBase.top,
    width,
    height: batteryBase.height * scale,
  }
}

/** The corner the pointer holds the icon by, kept on the screen and rounded to whole points. */
export function dragBattery(pointer: { x: number; y: number }, grab: { dx: number; dy: number }, b: BatteryState, screen: Screen): { x: number; y: number } {
  const box = batteryBox(b, screen)
  return {
    x: clamp(Math.round(pointer.x - grab.dx), 0, Math.max(0, screen.width - box.width)),
    y: clamp(Math.round(pointer.y - grab.dy), 0, Math.max(0, screen.height - box.height)),
  }
}
