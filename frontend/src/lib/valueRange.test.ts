import { describe, expect, it } from 'vitest'
import { rangeClass, rangeKey, rangesByKey, rangeState } from '@/lib/valueRange'

describe('rangeState', () => {
  const range = { min: 18, max: 26 }
  it('is low below the range, high above it and ok inside, ends included', () => {
    expect(rangeState(17.9, range)).toBe('low')
    expect(rangeState(26.1, range)).toBe('high')
    expect(rangeState(18, range)).toBe('ok')
    expect(rangeState(26, range)).toBe('ok')
    expect(rangeState(22, range)).toBe('ok')
  })
  it('works with one end only and with none', () => {
    expect(rangeState(5, { min: null, max: 26 })).toBe('ok')
    expect(rangeState(30, { min: null, max: 26 })).toBe('high')
    expect(rangeState(5, { min: 18, max: null })).toBe('low')
    expect(rangeState(5, undefined)).toBe('ok')
  })
})

describe('rangeClass', () => {
  it('is light blue below, pink above and nothing inside', () => {
    expect(rangeClass('low')).toBe('text-sky-300')
    expect(rangeClass('high')).toBe('text-pink-300')
    expect(rangeClass('ok')).toBeUndefined()
  })
})

describe('rangesByKey', () => {
  it('finds a range by device and metric', () => {
    const map = rangesByKey([{ deviceId: 'meteo-out1', metric: 'temperature', min: 18, max: 26 }])
    expect(map.get(rangeKey('meteo-out1', 'temperature'))?.max).toBe(26)
    expect(map.get(rangeKey('meteo-out1', 'humidity'))).toBeUndefined()
  })
})
