import { ChevronRight, FolderInput, MoreVertical, Plus, Trash2 } from 'lucide-react'
import { useMemo, useState, type DragEvent } from 'react'
import { NavLink } from 'react-router'
import type { NoteNodeDto } from '@/api/client'
import { Button } from '@/components/ui/button'
import { t } from '@/i18n'
import { childrenOf, colorClass, dropPlace, dropTarget, type DropPlace, type NoteChildren } from '@/lib/notes'
import { cn } from '@/lib/utils'

type Drop = { id: number; place: DropPlace } | 'root' | null

const EXPANDED_KEY = 'hive.notes.expanded'

function loadExpanded(): Set<number> {
  try {
    return new Set(JSON.parse(localStorage.getItem(EXPANDED_KEY) ?? '[]') as number[])
  } catch {
    return new Set()
  }
}

/**
 * The tree of notes. Drag a row onto the top or bottom edge of another to put it before or after, onto its middle to
 * nest it, or onto the strip at the bottom to make it a root. Phones have no reliable drag and drop: the row menu
 * moves notes there ("Move to…").
 */
export function NoteTree({ nodes, selectedId, canEdit, onMove, onAddChild, onMoveTo, onDelete }: {
  nodes: NoteNodeDto[]
  selectedId?: number
  canEdit: boolean
  onMove: (id: number, parentId: number | null, index: number) => void
  onAddChild: (parentId: number) => void
  onMoveTo: (id: number) => void
  onDelete: (id: number) => void
}) {
  const children = useMemo(() => childrenOf(nodes), [nodes])
  const [expanded, setExpanded] = useState(loadExpanded)
  const [dragged, setDragged] = useState<number | null>(null)
  const [drop, setDrop] = useState<Drop>(null)
  const [menu, setMenu] = useState<number | null>(null)

  // The selected note's branch opens, so a note picked from search or opened by link is visible in the tree.
  const open = useMemo(() => {
    const ids = new Set(expanded)
    const byId = new Map(nodes.map((n) => [n.id, n]))
    let parent = selectedId != null ? byId.get(selectedId)?.parentId : null
    while (parent != null && !ids.has(parent)) {
      ids.add(parent)
      parent = byId.get(parent)?.parentId ?? null
    }
    return ids
  }, [expanded, nodes, selectedId])

  function toggle(id: number) {
    setExpanded((prev) => {
      const next = new Set(prev)
      if (!next.delete(id)) next.add(id)
      try {
        localStorage.setItem(EXPANDED_KEY, JSON.stringify([...next]))
      } catch {
        // Private mode: the tree simply forgets what was open.
      }
      return next
    })
  }

  function onDragOver(e: DragEvent, id: number) {
    if (dragged == null) return
    const rect = e.currentTarget.getBoundingClientRect()
    const place = dropPlace(e.clientY - rect.top, rect.height)
    if (!dropTarget(nodes, dragged, id, place)) return
    e.preventDefault()
    e.dataTransfer.dropEffect = 'move'
    if (!drop || drop === 'root' || drop.id !== id || drop.place !== place) setDrop({ id, place })
  }

  function onDrop(e: DragEvent) {
    e.preventDefault()
    if (dragged == null || drop == null) return
    if (drop === 'root') onMove(dragged, null, children.get(null)?.filter((n) => n.id !== dragged).length ?? 0)
    else {
      const target = dropTarget(nodes, dragged, drop.id, drop.place)
      if (target) {
        onMove(dragged, target.parentId, target.index)
        if (drop.place === 'inside' && !open.has(drop.id)) toggle(drop.id)
      }
    }
    endDrag()
  }

  function endDrag() {
    setDragged(null)
    setDrop(null)
  }

  const rows = (parentId: number | null, depth: number) =>
    (children.get(parentId) ?? []).map((node) => (
      <Row
        key={node.id}
        node={node}
        depth={depth}
        childrenMap={children}
        open={open.has(node.id)}
        selected={node.id === selectedId}
        dragging={dragged === node.id}
        drop={drop && drop !== 'root' && drop.id === node.id ? drop.place : null}
        canEdit={canEdit}
        menuOpen={menu === node.id}
        onToggle={() => toggle(node.id)}
        onMenu={(show) => setMenu(show ? node.id : null)}
        onDragStart={(e) => {
          e.dataTransfer.effectAllowed = 'move'
          e.dataTransfer.setData('text/plain', String(node.id))
          setDragged(node.id)
        }}
        onDragOver={(e) => onDragOver(e, node.id)}
        onDrop={onDrop}
        onDragEnd={endDrag}
        onAddChild={() => {
          setMenu(null)
          if (!open.has(node.id)) toggle(node.id)
          onAddChild(node.id)
        }}
        onMoveTo={() => {
          setMenu(null)
          onMoveTo(node.id)
        }}
        onDelete={() => {
          setMenu(null)
          onDelete(node.id)
        }}
      >
        {open.has(node.id) && rows(node.id, depth + 1)}
      </Row>
    ))

  return (
    <div onDragLeave={(e) => !e.currentTarget.contains(e.relatedTarget as Node) && setDrop(null)}>
      <ul role="tree" className="space-y-0.5">{rows(null, 0)}</ul>
      {canEdit && dragged != null && (
        <div
          className={cn(
            'mt-2 rounded-lg border border-dashed px-3 py-3 text-center text-xs',
            drop === 'root' ? 'border-amber-400 bg-amber-400/10 text-amber-300' : 'border-neutral-700 text-neutral-500',
          )}
          onDragOver={(e) => {
            e.preventDefault()
            setDrop('root')
          }}
          onDrop={onDrop}
        >
          {t('notes.dropRoot')}
        </div>
      )}
    </div>
  )
}

function Row({
  node, depth, childrenMap, open, selected, dragging, drop, canEdit, menuOpen, children,
  onToggle, onMenu, onDragStart, onDragOver, onDrop, onDragEnd, onAddChild, onMoveTo, onDelete,
}: {
  node: NoteNodeDto
  depth: number
  childrenMap: NoteChildren
  open: boolean
  selected: boolean
  dragging: boolean
  drop: DropPlace | null
  canEdit: boolean
  menuOpen: boolean
  children: React.ReactNode
  onToggle: () => void
  onMenu: (show: boolean) => void
  onDragStart: (e: DragEvent) => void
  onDragOver: (e: DragEvent) => void
  onDrop: (e: DragEvent) => void
  onDragEnd: () => void
  onAddChild: () => void
  onMoveTo: () => void
  onDelete: () => void
}) {
  const hasChildren = (childrenMap.get(node.id)?.length ?? 0) > 0
  const bookmark = colorClass(node.color)
  return (
    <li role="treeitem" aria-expanded={hasChildren ? open : undefined} aria-selected={selected}>
      <div
        draggable={canEdit}
        onDragStart={onDragStart}
        onDragOver={onDragOver}
        onDrop={onDrop}
        onDragEnd={onDragEnd}
        className={cn(
          'group relative flex items-center gap-1 rounded-lg pr-1 text-sm',
          selected ? 'bg-neutral-800 text-neutral-50' : 'text-neutral-300 hover:bg-neutral-800/60',
          dragging && 'opacity-40',
          drop === 'inside' && 'bg-amber-400/15 ring-1 ring-amber-400',
        )}
        style={{ paddingLeft: depth * 16 }}
      >
        {drop === 'before' && <span className="pointer-events-none absolute inset-x-0 -top-px h-0.5 rounded bg-amber-400" />}
        {drop === 'after' && <span className="pointer-events-none absolute inset-x-0 -bottom-px h-0.5 rounded bg-amber-400" />}
        <button
          type="button"
          onClick={onToggle}
          className={cn('flex size-7 shrink-0 items-center justify-center text-neutral-500', !hasChildren && 'invisible')}
          aria-label={open ? t('notes.collapse') : t('notes.expand')}
        >
          <ChevronRight className={cn('size-4 transition-transform', open && 'rotate-90')} />
        </button>
        <NavLink to={`/notes/${node.id}`} className="flex min-w-0 flex-1 items-center gap-2 py-1.5" draggable={false}>
          <span className={cn('h-4 w-1 shrink-0 rounded-full', bookmark ?? 'bg-transparent')} />
          {node.icon && <span className="shrink-0">{node.icon}</span>}
          <span className="truncate">{node.title}</span>
        </NavLink>
        {canEdit && (
          <div className="relative">
            <button
              type="button"
              onClick={() => onMenu(!menuOpen)}
              className="flex size-7 items-center justify-center rounded text-neutral-500 hover:text-neutral-200 md:opacity-0 md:group-hover:opacity-100 md:focus:opacity-100"
              aria-label={t('notes.menu')}
            >
              <MoreVertical className="size-4" />
            </button>
            {menuOpen && (
              <>
                <div className="fixed inset-0 z-30" onClick={() => onMenu(false)} />
                <div className="absolute right-0 z-40 mt-1 w-52 rounded-lg border border-neutral-700 bg-neutral-900 p-1 shadow-xl">
                  <MenuItem icon={<Plus />} label={t('notes.newChild')} onClick={onAddChild} />
                  <MenuItem icon={<FolderInput />} label={t('notes.moveTo')} onClick={onMoveTo} />
                  <MenuItem icon={<Trash2 />} label={t('notes.delete')} onClick={onDelete} danger />
                </div>
              </>
            )}
          </div>
        )}
      </div>
      {open && hasChildren && <ul role="group" className="space-y-0.5">{children}</ul>}
    </li>
  )
}

function MenuItem({ icon, label, onClick, danger }: { icon: React.ReactNode; label: string; onClick: () => void; danger?: boolean }) {
  return (
    <Button variant="ghost" size="sm" onClick={onClick} className={cn('w-full justify-start', danger && 'text-red-400 hover:text-red-300')}>
      {icon}
      {label}
    </Button>
  )
}
