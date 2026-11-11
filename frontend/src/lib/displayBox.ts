import { type Row } from '@/lib/displayRows'
import { labelHeight, pixelHeight } from '@/lib/display'

let measuring: CanvasRenderingContext2D | null | undefined

/** The width in screen points of a text set in a sans-serif font of <px> points: close to the hive's font, a guide only. */
export function textWidth(text: string, px: number, bold: boolean, italic: boolean): number {
  if (measuring === undefined) {
    try {
      measuring = document.createElement('canvas').getContext('2d')
    } catch {
      measuring = null
    }
  }
  if (!measuring) return text.length * px * 0.55
  measuring.font = `${italic ? 'italic ' : ''}${bold ? 'bold ' : ''}${px}px sans-serif`
  return measuring.measureText(text).width
}

/** The room a row takes: the width of its text or caption and its height with the caption (the value's capitals are about 0.72 of its font). */
export function rowBox(row: Row, text: string, screenHeight: number): { width: number; height: number } {
  const labeled = !!row.label && row.text === null
  const cap = row.px === null ? pixelHeight(row.size, screenHeight) : row.px * 0.72
  const labelPx = row.px === null ? 7 : Math.max(6, Math.round(row.px / 2))
  const width = Math.max(textWidth(text, cap / 0.72, row.bold, row.italic), labeled ? textWidth(row.label, labelPx, false, false) : 0)
  const height = !labeled ? cap : (row.px === null ? labelHeight : labelPx * 0.72 + Math.max(2, labelPx / 3)) + cap
  return { width, height }
}

export const clampInt = (v: string, min: number, max: number) => Math.min(max, Math.max(min, Math.round(Number(v) || 0)))
