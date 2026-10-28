import { ChevronDown } from 'lucide-react'
import { useEffect, useState } from 'react'
import { cn } from '@/lib/utils'

/** One source in the picker: its title and what can be shown from it. */
export interface SourceGroup {
  /** '' is the device itself; 'clock' is the hive's clock. */
  id: string
  title: string
  items: { value: string; label: string }[]
}

/**
 * Where a row's value comes from, in one place: a button with the current choice and, open, a tree of the sources with
 * what each can show. Only the leaves are picked; the source is their title.
 */
export function SourcePicker({ groups, source, value, text, disabled, onPick }: {
  groups: SourceGroup[]
  source: string
  value: string
  /** What the button says now. */
  text: string
  disabled?: boolean
  onPick: (source: string, value: string) => void
}) {
  const [open, setOpen] = useState(false)
  useEffect(() => {
    if (!open) return
    const close = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false) }
    window.addEventListener('keydown', close)
    return () => window.removeEventListener('keydown', close)
  }, [open])
  return (
    <div className="relative">
      <button type="button" disabled={disabled} aria-expanded={open} onClick={() => setOpen(!open)}
        className="flex h-9 w-full items-center justify-between gap-2 rounded-lg border border-neutral-700 bg-neutral-900 px-2 text-left text-sm text-neutral-200 focus:border-amber-400 focus:outline-none disabled:opacity-50">
        <span className="truncate">{text}</span>
        <ChevronDown className="size-4 shrink-0 text-neutral-500" />
      </button>
      {open && (
        <>
          <div className="fixed inset-0 z-20" onClick={() => setOpen(false)} />
          <div role="tree" className="absolute left-0 z-30 mt-1 max-h-80 w-72 max-w-[calc(100vw-2rem)] overflow-y-auto rounded-lg border border-neutral-700 bg-neutral-900 p-1 shadow-xl">
            {groups.map((g) => (
              <div key={g.id} role="group" aria-label={g.title} className="py-1">
                <p className="px-2 pb-1 text-xs font-medium text-neutral-500">{g.title}</p>
                {g.items.map((item) => {
                  const chosen = g.id === source && item.value === value
                  return (
                    <button key={item.value} type="button" role="treeitem" aria-selected={chosen}
                      onClick={() => { onPick(g.id, item.value); setOpen(false) }}
                      className={cn('block w-full rounded-md py-1.5 pl-5 pr-2 text-left text-sm',
                        chosen ? 'bg-amber-400/15 text-amber-300' : 'text-neutral-200 hover:bg-neutral-800')}>
                      {item.label}
                    </button>
                  )
                })}
              </div>
            ))}
          </div>
        </>
      )}
    </div>
  )
}
