import { useRef, type PointerEvent as ReactPointerEvent } from 'react'
import { HelpTip } from '@/components/HelpTip'
import { t } from '@/i18n'
import { rowBox } from '@/lib/displayBox'
import { type Row } from '@/lib/displayRows'
import { batteryBase, type BatteryState, batteryBox, dragBattery, dragRow, pointToScreen, labelHeight, pixelHeight, type Screen, textAnchor } from '@/lib/display'

export function FieldLabel({ text, help }: { text: string; help: string }) {
  return <span className="flex items-center gap-1 text-xs text-neutral-400">{text}<HelpTip text={help} /></span>
}


/**
 * What the screen will look like, drawn at its own pixels and scaled up. Placed rows can be dragged to their spot; the
 * fonts are close to the screen's, not the same, so the picture is a guide. As an <overlay> it lies over the picture the
 * hive drew: the rows are marks to drag where they are now, the picture under them shows them where they were saved.
 */
export function ScreenPreview({ screen, rows, texts, onMove, editable, overlay = false, battery = null, onBattery }: {
  screen: Screen; rows: Row[]; texts: string[]; onMove: (i: number, x: number, y: number) => void; editable: boolean; overlay?: boolean
  /** The battery icon of the layout (a screen the hive draws without tabs): a box to drag, in the screen's own points. */
  battery?: BatteryState | null; onBattery?: (x: number, y: number) => void
}) {
  const svg = useRef<SVGSVGElement>(null)
  /** What is held: a row (by its number) or the battery icon, and where it was taken, as the offset from its anchor. */
  const drag = useRef<{ i: number | 'battery'; dx: number; dy: number } | null>(null)

  /** The screen point under the pointer: the preview may be scaled to any size, the box of the picture says how much. */
  function point(e: ReactPointerEvent) {
    const box = svg.current!.getBoundingClientRect()
    return pointToScreen(e.clientX, e.clientY, box, screen)
  }

  return (
    <svg ref={svg} viewBox={`0 0 ${screen.width} ${screen.height}`} role="img" aria-label={t('display.preview')}
      className={overlay ? 'absolute left-px top-px h-[calc(100%-2px)] w-[calc(100%-2px)] touch-none select-none' : 'w-full max-w-72 touch-none select-none rounded border border-neutral-700 bg-black'}
      style={overlay ? undefined : { aspectRatio: `${screen.width} / ${screen.height}` }}
      onPointerMove={(e) => {
        if (!drag.current) return
        const p = point(e)
        const held = drag.current
        if (held.i === 'battery') {
          if (!battery || !onBattery) return
          const at = dragBattery(p, held, battery, screen)
          onBattery(at.x, at.y)
          return
        }
        // the anchor of a row is the left edge, the middle or the right edge of its text: the range depends on it, and on the text's width
        const row = rows[held.i]
        if (!row) return
        const size = rowBox(row, texts[held.i] ?? '', screen.height)
        const at = dragRow(p, held, row.align, size.width, size.height, screen)
        onMove(held.i, at.x, at.y)
      }}
      onPointerUp={() => { drag.current = null }}
      onPointerLeave={() => { drag.current = null }}>
      {rows.map((row, i) => {
        const labeled = !!row.label && row.text === null
        // px is the font size, its capitals about 0.72 of it, the label in half of it (as the hive draws, DisplayRender.cs);
        // without px the size is the height of the capitals
        const labelPx = row.px === null ? 7 : Math.max(6, Math.round(row.px / 2))
        const valueTop = row.y + (!labeled ? 0 : row.px === null ? labelHeight : labelPx * 0.72 + Math.max(2, labelPx / 3))
        const cap = row.px === null ? pixelHeight(row.size, screen.height) : row.px * 0.72
        return (
          <g key={i} className={editable ? 'cursor-move' : undefined}
            onPointerDown={(e) => {
              if (!editable) return
              const p = point(e)
              drag.current = { i, dx: p.x - row.x, dy: p.y - row.y }
              svg.current?.setPointerCapture(e.pointerId)
            }}>
            {labeled && (
              <text x={row.x} y={row.px === null ? row.y : row.y + labelPx * 0.72} dominantBaseline={row.px === null ? 'hanging' : 'alphabetic'} textAnchor={textAnchor(row.align)} fontSize={labelPx} fill={overlay ? '#f59e0b' : '#fff'} fillOpacity={overlay ? 0.5 : 1} fontFamily="monospace">{row.label}</text>
            )}
            {/* the anchor: where x and y are (the left edge, the middle or the right edge of the text, by its alignment) */}
            <path d={`M${row.x - 3} ${row.y}h6M${row.x} ${row.y - 3}v6`} stroke={overlay ? '#f59e0b' : '#60a5fa'} strokeWidth={0.8} fill="none" />
            {/* digits take about 0.72 of the font size from the top */}
            <text x={row.x} y={row.px === null ? valueTop : valueTop + cap} dominantBaseline={row.px === null ? 'hanging' : 'alphabetic'} textAnchor={textAnchor(row.align)} fontSize={cap / 0.72} fill={overlay ? '#f59e0b' : '#fff'} fillOpacity={overlay ? 0.5 : 1} fontFamily="sans-serif" fontWeight={row.bold ? 700 : 400} fontStyle={row.italic ? 'italic' : 'normal'}>{texts[i]}</text>
          </g>
        )
      })}
      {battery && battery.enabled && (() => {
        const box = batteryBox(battery, screen)
        return (
          <g className={editable ? 'cursor-move' : undefined}
            onPointerDown={(e) => {
              if (!editable) return
              const p = point(e)
              drag.current = { i: 'battery', dx: p.x - box.left, dy: p.y - box.top }
              svg.current?.setPointerCapture(e.pointerId)
            }}>
            <rect x={box.left} y={box.top} width={box.width} height={box.height} fill="#f59e0b" fillOpacity={0.25} stroke="#f59e0b" strokeWidth={0.8} />
            {/* the nub, as drawn over the picture */}
            <rect x={box.left + (3 * box.width) / batteryBase.width} y={box.top} width={(4 * box.width) / batteryBase.width} height={(2 * box.height) / batteryBase.height} fill="#f59e0b" fillOpacity={0.6} />
          </g>
        )
      })()}
    </svg>
  )
}
