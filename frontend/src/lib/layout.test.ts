import { describe, expect, it } from 'vitest'
import { arrange, moveBy, moveTo } from './layout'

const available = [{ id: 'guard', w: 4 }, { id: 'swarm', w: 4 }, { id: 'zone:1', w: 6 }, { id: 'cam:hall', w: 4 }, { id: 'events', w: 6 }]
const ids = (list: { id: string }[]) => list.map((w) => w.id)

describe('overview layout', () => {
  it('uses the default order while nothing is saved', () => {
    expect(arrange(null, available)).toEqual(available.map((a) => ({ ...a, hidden: false })))
  })

  it('keeps the saved order, drops what is gone and puts what is new after its default neighbour', () => {
    const saved = [{ id: 'events', w: 12 }, { id: 'zone:9', w: 6 }, { id: 'guard', w: 8, hidden: true }, { id: 'zone:1', w: 6 }]
    const list = arrange(saved, available)
    expect(ids(list)).toEqual(['events', 'guard', 'swarm', 'zone:1', 'cam:hall'])
    expect(list[0].w).toBe(12)
    expect(list[1].hidden).toBe(true)
  })

  it('moves a widget to another one\'s place and by one step', () => {
    const list = arrange(null, available)
    expect(ids(moveTo(list, 'events', 'guard'))).toEqual(['events', 'guard', 'swarm', 'zone:1', 'cam:hall'])
    expect(ids(moveBy(list, 'guard', 1))).toEqual(['swarm', 'guard', 'zone:1', 'cam:hall', 'events'])
    expect(ids(moveBy(list, 'guard', -1))).toEqual(ids(list))
  })
})
