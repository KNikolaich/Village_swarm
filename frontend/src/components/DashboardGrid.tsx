import { ArrowDown, ArrowUp, Eye, EyeOff, GripVertical } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { Button } from '@/components/ui/button'
import { Select } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { moveBy, moveTo, widths, type Widget } from '@/lib/layout'
import { cn } from '@/lib/utils'

// Written out in full so Tailwind generates them.
const span: Record<number, string> = {
  3: 'lg:col-span-3',
  4: 'lg:col-span-4',
  6: 'lg:col-span-6',
  8: 'lg:col-span-8',
  12: 'lg:col-span-12',
}

export interface WidgetView {
  id: string
  /** Named in the editing bar. */
  title: string
  node: ReactNode
}

/**
 * The overview's widgets in rows of twelve columns (one column on a phone). While arranging, each widget gets a bar:
 * a handle to drag it to another place, arrows (a phone has no drag), its width on a wide screen, hide or show.
 */
export function DashboardGrid({ views, layout, phone, editing, onChange }: {
  views: readonly WidgetView[]
  layout: readonly Widget[]
  /** One column, no widths: the phone arrangement (also when previewed on a wide screen). */
  phone: boolean
  editing: boolean
  onChange: (layout: Widget[]) => void
}) {
  const [dragged, setDragged] = useState<string | null>(null)
  const byId = new Map(views.map((v) => [v.id, v]))
  const shown = layout.filter((w) => byId.has(w.id) && (editing || !w.hidden))
  const set = (id: string, patch: Partial<Widget>) => onChange(layout.map((w) => (w.id === id ? { ...w, ...patch } : w)))

  return (
    <div className={cn('grid grid-cols-1 gap-3', !phone && 'lg:grid-cols-12', phone && editing && 'mx-auto max-w-sm')}>
      {shown.map((w, i) => {
        const view = byId.get(w.id)!
        return (
          <div
            key={w.id}
            className={cn('flex min-w-0 flex-col', !phone && span[w.w], editing && 'rounded-xl ring-1 ring-amber-400/30', editing && w.hidden && 'opacity-40', dragged === w.id && 'opacity-60')}
            onDragOver={(e) => {
              if (!dragged) return
              e.preventDefault()
              if (dragged !== w.id) onChange(moveTo(layout, dragged, w.id))
            }}
            onDrop={(e) => e.preventDefault()}
          >
            {editing && (
              <div className="flex items-center gap-1 rounded-t-xl bg-amber-400/10 px-1.5 py-1 text-xs">
                <span
                  draggable
                  onDragStart={(e) => {
                    e.dataTransfer.effectAllowed = 'move'
                    e.dataTransfer.setData('text/plain', w.id)
                    setDragged(w.id)
                  }}
                  onDragEnd={() => setDragged(null)}
                  className="cursor-grab p-1 text-neutral-400 active:cursor-grabbing"
                  title={t('layout.drag')}
                >
                  <GripVertical className="size-4" />
                </span>
                <span className="min-w-0 flex-1 truncate text-neutral-300">{view.title}</span>
                <Button variant="ghost" size="icon" className="size-7" disabled={i === 0} aria-label={t('layout.up')} onClick={() => onChange(moveBy(layout, w.id, -1))}><ArrowUp /></Button>
                <Button variant="ghost" size="icon" className="size-7" disabled={i === shown.length - 1} aria-label={t('layout.down')} onClick={() => onChange(moveBy(layout, w.id, 1))}><ArrowDown /></Button>
                {!phone && (
                  <Select value={w.w} onChange={(e) => set(w.id, { w: Number(e.target.value) })} className="h-7 px-1 text-xs" aria-label={t('layout.width')}>
                    {widths.map((n) => <option key={n} value={n}>{t(`layout.widths.${n}`)}</option>)}
                  </Select>
                )}
                <Button
                  variant="ghost"
                  size="icon"
                  className="size-7"
                  aria-label={w.hidden ? t('layout.show') : t('layout.hide')}
                  title={w.hidden ? t('layout.show') : t('layout.hide')}
                  onClick={() => set(w.id, { hidden: !w.hidden })}
                >
                  {w.hidden ? <EyeOff /> : <Eye />}
                </Button>
              </div>
            )}
            <div className={cn('flex-1 [&>*]:h-full', editing && 'pointer-events-none select-none')}>{view.node}</div>
          </div>
        )
      })}
    </div>
  )
}
