import { describe, expect, it } from 'vitest'
import { calibratedOffset, parseNtcNumber } from './ntc'

describe('ntc settings', () => {
  it('reads numbers with a comma, keeps empty as the default and refuses nonsense', () => {
    expect(parseNtcNumber('vcc', '3,28')).toBe(3.28)
    expect(parseNtcNumber('offset', '−0.5')).toBe(-0.5)
    expect(parseNtcNumber('rSeries', ' ')).toBeNull()
    expect(parseNtcNumber('beta', 'abc')).toBeUndefined()
    expect(parseNtcNumber('beta', '50')).toBeUndefined()
    expect(parseNtcNumber('offset', '25')).toBeUndefined()
  })

  it('fits the offset to a reference thermometer', () => {
    expect(calibratedOffset(null, 23.4, 22.8)).toBe(-0.6)
    // The reading already has the old offset in it: the new one adds the remaining difference.
    expect(calibratedOffset(-0.6, 22.9, 22.8)).toBe(-0.7)
    expect(calibratedOffset(0.3, 20, 20)).toBe(0.3)
  })
})
