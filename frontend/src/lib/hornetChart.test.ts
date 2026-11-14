import { describe, expect, it } from 'vitest'
import { axisColor, chunkIndexes, mergeChunks, panEnd, splitSegments, tint, zoomTo } from './hornetChart'

const H = 3_600_000
const pt = (min: number) => ({ ts: new Date(min * 60_000).toISOString() })

describe('chunkIndexes', () => {
  it('covers the window and one chunk ahead', () => {
    expect(chunkIndexes(10 * H, 12 * H, H)).toEqual([9, 10, 11])
  })
  it('a window ending exactly on a border does not need the next chunk', () => {
    expect(chunkIndexes(24 * H, 48 * H, 24 * H, 0)).toEqual([1])
  })
})

describe('mergeChunks', () => {
  it('sorts and keeps a duplicated point once', () => {
    expect(mergeChunks([[pt(5), pt(6)], undefined, [pt(1), pt(5)]]).map((p) => p.ts)).toEqual([pt(1), pt(5), pt(6)].map((p) => p.ts))
  })
})

describe('splitSegments', () => {
  it('cuts at a long silence', () => {
    const pts = [0, 5, 10, 15, 20, 300, 305].map(pt)
    expect(splitSegments(pts).map((s) => s.length)).toEqual([5, 2])
  })
  it('keeps a steady series whole', () => {
    expect(splitSegments([0, 5, 10].map(pt))).toHaveLength(1)
    expect(splitSegments([])).toEqual([])
  })
})

describe('panEnd / zoomTo', () => {
  it('stops at the present as "follow now"', () => {
    expect(panEnd({ hours: 1, end: 1000 }, 500, 5000)).toBe(1500)
    expect(panEnd({ hours: 1, end: 4900 }, 500, 5000)).toBeNull()
    expect(panEnd({ hours: 1, end: null }, -H, 10 * H)).toBe(9 * H)
  })
  it('zooming keeps the centre', () => {
    const view = zoomTo({ hours: 24, end: 100 * H }, 6, 200 * H)
    expect(view).toEqual({ hours: 6, end: 100 * H - 9 * H })
  })
  it('a followed view keeps following', () => {
    expect(zoomTo({ hours: 24, end: null }, 6, 200 * H)).toEqual({ hours: 6, end: null })
  })
})

describe('colours', () => {
  it('first line of an axis is the pure colour, later ones lighter', () => {
    expect(tint('#fff', 0)).toEqual({ color: '#fff', dashed: false })
    expect(tint('#000', 2).dashed).toBe(true)
  })
  it('fixed colours for usual units', () => {
    expect(axisColor('V', 3)).toBe('#34d399')
    expect(axisColor('hPa', 1)).toBe('#f472b6')
  })
})
