import { describe, expect, it } from 'vitest'
import type { NoteNodeDto } from '@/api/client'
import { ancestors, childrenOf, dropPlace, dropTarget, moveInTree } from './notes'

const node = (id: number, parentId: number | null, sort: number, title = `n${id}`): NoteNodeDto => ({
  id, parentId, sort, title, icon: null, color: null, updatedAt: '2026-10-20T00:00:00Z',
})

// 1 Разработка ─ 2 Улучшения ─ 4 OTA
//               └ 3 Баги
// 5 Сад
const tree = [node(1, null, 0), node(2, 1, 0), node(3, 1, 1), node(4, 2, 0), node(5, null, 1)]
const titles = (nodes: NoteNodeDto[], parent: number | null) => childrenOf(nodes).get(parent)?.map((n) => n.id) ?? []

describe('notes tree', () => {
  it('drops before, inside and after a row by where the pointer is', () => {
    expect(dropPlace(2, 40)).toBe('before')
    expect(dropPlace(20, 40)).toBe('inside')
    expect(dropPlace(38, 40)).toBe('after')
  })

  it('works out the new parent and index the way the server counts them', () => {
    expect(dropTarget(tree, 5, 3, 'before')).toEqual({ parentId: 1, index: 1 })
    expect(dropTarget(tree, 5, 3, 'after')).toEqual({ parentId: 1, index: 2 })
    expect(dropTarget(tree, 5, 2, 'inside')).toEqual({ parentId: 2, index: 1 })
    // Moving down within the same parent: the dragged note does not count among the siblings.
    expect(dropTarget(tree, 2, 3, 'after')).toEqual({ parentId: 1, index: 1 })
  })

  it('never drops a note into its own branch', () => {
    expect(dropTarget(tree, 1, 4, 'inside')).toBeNull()
    expect(dropTarget(tree, 1, 1, 'after')).toBeNull()
  })

  it('moves a branch locally like the server does', () => {
    const moved = moveInTree(tree, 2, null, 0)
    expect(titles(moved, null)).toEqual([2, 1, 5])
    expect(titles(moved, 1)).toEqual([3])
    expect(moved.find((n) => n.id === 3)?.sort).toBe(0)
    expect(titles(moved, 2)).toEqual([4])
  })

  it('lists ancestors from the root down', () => {
    expect(ancestors(tree, 4).map((n) => n.id)).toEqual([1, 2])
    expect(ancestors(tree, 5)).toEqual([])
  })
})
