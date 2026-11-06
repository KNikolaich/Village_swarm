import { describe, expect, it } from 'vitest'
import { isStale, staleAfterMs } from './stale'

describe('isStale', () => {
  const now = Date.parse('2026-11-06T12:00:00Z')
  it('is false for a fresh reading, whatever the hornet status says', () => {
    expect(isStale(new Date(now - 7 * 60_000).toISOString(), now)).toBe(false)
  })
  it('is true for an old reading or none', () => {
    expect(isStale(new Date(now - staleAfterMs - 1000).toISOString(), now)).toBe(true)
    expect(isStale(null, now)).toBe(true)
  })
})
