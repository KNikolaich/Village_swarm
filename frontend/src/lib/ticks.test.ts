import { describe, expect, it } from 'vitest'
import { niceTicks, tickDecimals, timeTicks } from './ticks'

describe('niceTicks', () => {
  it('uses round steps that cover the data', () => {
    const r = niceTicks(36.5, 48.2)
    expect(r.ticks).toEqual([35, 40, 45, 50])
    expect(r.min).toBeLessThanOrEqual(36.5)
    expect(r.max).toBeGreaterThanOrEqual(48.2)
  })

  it('goes to tenths for a narrow range and keeps negatives', () => {
    expect(niceTicks(45.1, 46.1).ticks).toEqual([45, 45.5, 46, 46.5])
    expect(niceTicks(-5.9, 1.8).ticks).toEqual([-6, -4, -2, 0, 2])
  })

  it('gives a usable range for a flat line', () => {
    const r = niceTicks(3, 3)
    expect(r.max).toBeGreaterThan(r.min)
    expect(r.ticks).toContain(3)
  })

  it('has no float noise in the marks', () => {
    for (const t of niceTicks(0.1, 0.9).ticks) expect(String(t).length).toBeLessThan(6)
  })
})

describe('tickDecimals', () => {
  it('matches the spacing', () => {
    expect([tickDecimals(5), tickDecimals(1), tickDecimals(0.5), tickDecimals(0.05)]).toEqual([0, 0, 1, 2])
  })
})

describe('timeTicks', () => {
  const at = (iso: string) => new Date(iso)

  it('marks whole hours across six hours', () => {
    const { ticks, stepMs } = timeTicks(at('2026-10-14T05:20:00'), at('2026-10-14T11:20:00'))
    expect(stepMs).toBe(3_600_000)
    expect(ticks.map((d) => d.getMinutes())).toEqual(ticks.map(() => 0))
    expect(ticks.length).toBeGreaterThanOrEqual(5)
  })

  it('marks local midnights for day-sized steps', () => {
    const { ticks, stepMs } = timeTicks(at('2026-10-07T12:00:00'), at('2026-10-14T12:00:00'))
    expect(stepMs).toBeGreaterThanOrEqual(86_400_000)
    for (const d of ticks) expect(d.getHours()).toBe(0)
  })

  it('keeps every mark inside the window and few enough to read', () => {
    for (const hours of [6, 24, 168, 720]) {
      const to = at('2026-10-14T11:20:00')
      const from = new Date(to.getTime() - hours * 3_600_000)
      const { ticks } = timeTicks(from, to)
      expect(ticks.length).toBeGreaterThanOrEqual(2)
      expect(ticks.length).toBeLessThanOrEqual(8)
      for (const d of ticks) {
        expect(d.getTime()).toBeGreaterThanOrEqual(from.getTime())
        expect(d.getTime()).toBeLessThanOrEqual(to.getTime())
      }
    }
  })
})
