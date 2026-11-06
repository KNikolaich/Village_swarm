import { describe, expect, it } from 'vitest'
import { takenBefore } from './format'

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
