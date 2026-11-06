import { describe, expect, it } from 'vitest'
import { isBlank, orDash, takenBefore } from './format'

describe('takenBefore', () => {
  const asked = '2026-10-19T00:00:03Z'
  it('passes frames of the burst and clock jitter as fresh', () => {
    expect(takenBefore('2026-10-19T00:00:05Z', asked)).toBe(false)
    expect(takenBefore('2026-10-18T23:59:30Z', asked)).toBe(false)
  })
  it('flags a frame the camera took hours before it was asked', () => {
    expect(takenBefore('2026-10-18T20:00:00Z', asked)).toBe(true)
  })
})

describe('orDash', () => {
  it('shows the last known value, a dash for never known or empty', () => {
    expect(orDash('A0:B7:65:12:34:56')).toBe('A0:B7:65:12:34:56')
    expect(orDash('0.2.0+a1b2c3d')).toBe('0.2.0+a1b2c3d')
    expect(orDash(null)).toBe('—')
    expect(orDash(undefined)).toBe('—')
    expect(orDash('')).toBe('—')
    expect(orDash('  ')).toBe('—')
  })
  it('isBlank keeps numbers, zero included', () => {
    expect(isBlank(0)).toBe(false)
    expect(isBlank('')).toBe(true)
    expect(isBlank(null)).toBe(true)
  })
})
