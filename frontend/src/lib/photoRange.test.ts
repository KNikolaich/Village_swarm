import { describe, expect, it } from 'vitest'
import { dayRange, inDayRange, isSingleDay, rangeFromUrl } from './photoRange'

describe('photo day range', () => {
  it('orders the two ends, whichever was pressed first', () => {
    expect(dayRange('2026-03-12', '2026-03-10')).toEqual({ from: '2026-03-10', to: '2026-03-12' })
    expect(dayRange('2026-03-10', '2026-03-12')).toEqual({ from: '2026-03-10', to: '2026-03-12' })
  })

  it('a press and release on the same day is one day', () => {
    const r = dayRange('2026-03-10', '2026-03-10')
    expect(isSingleDay(r)).toBe(true)
    expect(inDayRange('2026-03-10', r)).toBe(true)
    expect(inDayRange('2026-03-11', r)).toBe(false)
  })

  it('both ends belong to the range, also across months', () => {
    const r = dayRange('2026-02-27', '2026-03-02')
    expect(['2026-02-26', '2026-02-27', '2026-03-01', '2026-03-02', '2026-03-03'].map((d) => inDayRange(d, r))).toEqual([false, true, true, true, false])
  })

  it('reads the address, and ignores a broken "to"', () => {
    expect(rangeFromUrl('2026-03-10', '2026-03-12')).toEqual({ from: '2026-03-10', to: '2026-03-12' })
    expect(rangeFromUrl('2026-03-10', null)).toEqual({ from: '2026-03-10', to: '2026-03-10' })
    expect(rangeFromUrl('2026-03-10', 'soon')).toEqual({ from: '2026-03-10', to: '2026-03-10' })
  })
})
