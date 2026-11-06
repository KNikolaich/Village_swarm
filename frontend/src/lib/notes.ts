import type { NoteNodeDto } from '@/api/client'

/** Bookmark colors (the backend keeps the key). */
export const NOTE_COLORS = ['red', 'orange', 'yellow', 'green', 'teal', 'blue', 'purple', 'gray'] as const
export type NoteColor = (typeof NOTE_COLORS)[number]

export const noteColorClass: Record<NoteColor, string> = {
  red: 'bg-red-500',
  orange: 'bg-orange-500',
  yellow: 'bg-yellow-400',
  green: 'bg-emerald-500',
  teal: 'bg-teal-400',
  blue: 'bg-sky-500',
  purple: 'bg-violet-500',
  gray: 'bg-neutral-400',
}

export const colorClass = (color: string | null | undefined) =>
  color && color in noteColorClass ? noteColorClass[color as NoteColor] : undefined

/** Quick picks for the note icon; any other emoji can be typed. */
export const NOTE_ICONS = [
  '📝', '📌', '💡', '✅', '⚠️', '🔥', '⭐', '❤️', '📅', '📎', '🔧', '🛠', '💻', '🐝', '🏠', '🌱',
  '🍅', '🥒', '🌡', '💧', '🔌', '📷', '🔒', '🚗', '🛒', '💰', '📚', '🎯', '❓', '🧪', '🐞', '🚀',
]

export type NoteChildren = Map<number | null, NoteNodeDto[]>

/** Children of every note (and of the root under null), each list in display order. */
export function childrenOf(nodes: NoteNodeDto[]): NoteChildren {
  const map: NoteChildren = new Map()
  for (const n of nodes) {
    const list = map.get(n.parentId) ?? []
    list.push(n)
    map.set(n.parentId, list)
  }
  for (const list of map.values()) list.sort((a, b) => a.sort - b.sort || a.id - b.id)
  return map
}

/** The note itself and every note under it. */
export function branchIds(children: NoteChildren, id: number): Set<number> {
  const ids = new Set<number>([id])
  const queue = [id]
  while (queue.length) {
    for (const child of children.get(queue.shift()!) ?? []) {
      if (ids.has(child.id)) continue
      ids.add(child.id)
      queue.push(child.id)
    }
  }
  return ids
}

/** Ancestors of a note from the root down (not including the note). */
export function ancestors(nodes: NoteNodeDto[], id: number): NoteNodeDto[] {
  const byId = new Map(nodes.map((n) => [n.id, n]))
  const path: NoteNodeDto[] = []
  let parent = byId.get(id)?.parentId ?? null
  while (parent != null && !path.some((p) => p.id === parent)) {
    const node = byId.get(parent)
    if (!node) break
    path.unshift(node)
    parent = node.parentId
  }
  return path
}

/** The same move the server makes: out of the old siblings, in at the index among the new ones, both renumbered. */
export function moveInTree(nodes: NoteNodeDto[], id: number, parentId: number | null, index: number): NoteNodeDto[] {
  const moving = nodes.find((n) => n.id === id)
  if (!moving) return nodes
  const children = childrenOf(nodes.filter((n) => n.id !== id))
  const sorts = new Map<number, { parentId: number | null; sort: number }>()
  const oldSiblings = children.get(moving.parentId) ?? []
  oldSiblings.forEach((n, i) => sorts.set(n.id, { parentId: n.parentId, sort: i }))
  const newSiblings = [...(children.get(parentId) ?? [])]
  newSiblings.splice(Math.max(0, Math.min(index, newSiblings.length)), 0, moving)
  newSiblings.forEach((n, i) => sorts.set(n.id, { parentId, sort: i }))
  return nodes.map((n) => (sorts.has(n.id) ? { ...n, ...sorts.get(n.id)! } : n))
}

export type DropPlace = 'before' | 'inside' | 'after'

/** Top quarter of a row drops before it, bottom quarter after it, the middle inside it. */
export function dropPlace(offsetY: number, height: number): DropPlace {
  if (offsetY < height * 0.25) return 'before'
  if (offsetY > height * 0.75) return 'after'
  return 'inside'
}

/** Where a dragged note lands when dropped at `place` of `target`, or null when that would put it inside its own branch. */
export function dropTarget(
  nodes: NoteNodeDto[],
  draggedId: number,
  targetId: number,
  place: DropPlace,
): { parentId: number | null; index: number } | null {
  const children = childrenOf(nodes)
  if (branchIds(children, draggedId).has(targetId)) return null
  const target = nodes.find((n) => n.id === targetId)
  if (!target) return null
  if (place === 'inside') {
    const kids = (children.get(targetId) ?? []).filter((n) => n.id !== draggedId)
    return { parentId: targetId, index: kids.length }
  }
  const siblings = (children.get(target.parentId) ?? []).filter((n) => n.id !== draggedId)
  const at = siblings.findIndex((n) => n.id === targetId)
  return { parentId: target.parentId, index: place === 'before' ? at : at + 1 }
}
