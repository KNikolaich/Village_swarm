import { describe, expect, it } from 'vitest'
import { activeTemplate, addTemplate, defaultState, parseState, removeTemplate, smoothPoints, smoothValues, templateName, updateTemplate } from './smoothing'

const pt = (min: number, v: number, ts: string) => ({ ts, avg: v, min: v - 1, max: v + 1 + min })

describe('smoothValues', () => {
  it('leaves a run alone with a window of 1 or a single value', () => {
    expect(smoothValues([1, 5, 2], 'sma', 1)).toEqual([1, 5, 2])
    expect(smoothValues([7], 'ema', 8)).toEqual([7])
  })
  it('SMA is centred and shorter at the ends', () => {
    expect(smoothValues([0, 3, 6, 9, 12], 'sma', 3)).toEqual([1.5, 3, 6, 9, 10.5])
  })
  it('median removes a single spike', () => {
    expect(smoothValues([1, 1, 100, 1, 1], 'median', 3)).toEqual([1, 1, 1, 1, 1])
  })
  it('EMA starts at the first value and follows with alpha 2/(n+1)', () => {
    const out = smoothValues([10, 20], 'ema', 3)
    expect(out[0]).toBe(10)
    expect(out[1]).toBeCloseTo(15)
  })
  it('WMA keeps a constant run constant and the length', () => {
    const out = smoothValues([4, 4, 4, 4, 4, 4], 'wma', 5)
    expect(out).toHaveLength(6)
    out.forEach((v) => expect(v).toBeCloseTo(4))
  })
  it('reduces the jitter of a noisy line', () => {
    const noisy = [10, 14, 9, 15, 10, 14, 9, 15]
    const range = (a: number[]) => Math.max(...a) - Math.min(...a)
    expect(range(smoothValues(noisy, 'sma', 5))).toBeLessThan(range(noisy))
  })
})

describe('smoothPoints', () => {
  const t = (m: number) => new Date(Date.UTC(2026, 0, 1, 0, m)).toISOString()
  it('returns the points untouched without a template', () => {
    const pts = [pt(0, 1, t(0)), pt(0, 9, t(1))]
    expect(smoothPoints(pts, null)).toBe(pts)
  })
  it('keeps min <= avg <= max and the timestamps', () => {
    const pts = [1, 9, 2, 8, 3].map((v, i) => pt(0, v, t(i)))
    const out = smoothPoints(pts, { kind: 'median', n: 3 })
    expect(out.map((p) => p.ts)).toEqual(pts.map((p) => p.ts))
    out.forEach((p) => { expect(p.min).toBeLessThanOrEqual(p.avg); expect(p.max).toBeGreaterThanOrEqual(p.avg) })
  })
  it('does not bridge a long gap', () => {
    const pts = [pt(0, 0, t(0)), pt(0, 0, t(1)), pt(0, 0, t(2)), pt(0, 100, t(600)), pt(0, 100, t(601)), pt(0, 100, t(602))]
    const out = smoothPoints(pts, { kind: 'sma', n: 5 })
    expect(out[2].avg).toBe(0)
    expect(out[3].avg).toBe(100)
  })
})

describe('templates', () => {
  it('names them like SMA(5)', () => expect(templateName({ kind: 'ema', n: 8 })).toBe('EMA(8)'))
  it('applies only while ticked and a template is selected', () => {
    expect(activeTemplate(defaultState)).toBeNull()
    expect(activeTemplate({ ...defaultState, enabled: true })?.id).toBe('sma5')
    expect(activeTemplate({ ...defaultState, enabled: true, selected: 'gone' })).toBeNull()
  })
  it('adds, edits and deletes; deleting the chosen one picks another', () => {
    let s = addTemplate(defaultState, 'wma', 11)
    expect(s.templates).toHaveLength(5)
    const added = s.templates[4]
    expect(s.selected).toBe(added.id)
    s = updateTemplate(s, added.id, { n: 500 })
    expect(s.templates[4].n).toBe(99)
    s = removeTemplate(s, added.id)
    expect(s.templates).toHaveLength(4)
    expect(s.selected).toBe('sma5')
  })
  it('survives broken saved data', () => {
    expect(parseState(null)).toEqual(defaultState)
    expect(parseState('{nope')).toEqual(defaultState)
    expect(parseState('{"templates":[{"id":"a","kind":"zzz","n":3},{"id":"b","kind":"ema","n":4}],"selected":"x","enabled":true}'))
      .toEqual({ templates: [{ id: 'b', kind: 'ema', n: 4 }], selected: 'b', enabled: true })
  })
})
