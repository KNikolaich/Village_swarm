import { describe, expect, it } from 'vitest'
import { pickAt } from '@/lib/chartPick'

const at = (minute: number) => new Date(Date.UTC(2026, 9, 24, 12, minute)).toISOString()
const series = (minutes: number[]) => minutes.map((m) => ({ ts: at(m), avg: m }))
const t = (minute: number) => new Date(at(minute)).getTime()

describe('pickAt', () => {
  const live = series([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10])
  const silent = series([0, 1, 2, 3, 4])   // went quiet after minute 4

  it('without a pointer is the newest point', () => {
    expect(pickAt(live, null)?.avg).toBe(10)
    expect(pickAt(silent, null)?.avg).toBe(4)
  })

  it('under the pointer is the nearest point', () => {
    expect(pickAt(live, t(6) + 20_000)?.avg).toBe(6)
  })

  it('is nothing after a series stopped, while another series still has data', () => {
    expect(pickAt(silent, t(9))).toBeNull()
    expect(pickAt(live, t(9))?.avg).toBe(9)
  })

  it('is nothing before a series began, and nothing for an empty series', () => {
    expect(pickAt(series([5, 6, 7]), t(0))).toBeNull()
    expect(pickAt([], t(1))).toBeNull()
  })

  it('still finds the last point a moment after the series ends', () => {
    expect(pickAt(silent, t(5))?.avg).toBe(4)
  })
})
