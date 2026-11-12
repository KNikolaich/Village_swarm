import { describe, expect, it } from 'vitest'
import { setLanguage } from '@/i18n'
import { formatDuration, isBlank, orDash, takenBefore } from './format'

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

describe('formatDuration', () => {
  it('declines Russian units by the number', () => {
    setLanguage('ru')
    expect(formatDuration(1)).toBe('1 минуту')
    expect(formatDuration(5)).toBe('5 минут')
    expect(formatDuration(22)).toBe('22 минуты')
    expect(formatDuration(224)).toBe('3 часа 44 минуты')
    expect(formatDuration(60)).toBe('1 час')
    expect(formatDuration(1500)).toBe('1 сутки 1 час')
    expect(formatDuration(5 * 1440)).toBe('5 суток')
    expect(formatDuration(14 * 1440 + 2 * 1440)).toBe('2 недели 2 суток')
    expect(formatDuration(60 * 1440)).toBe('2 месяца')
    expect(formatDuration(11 * 60 + 5)).toBe('11 часов 5 минут')
  })
  it('reads English with singular and plural', () => {
    setLanguage('en')
    expect(formatDuration(1)).toBe('1 minute')
    expect(formatDuration(224)).toBe('3 hours 44 minutes')
    expect(formatDuration(1440)).toBe('1 day')
    setLanguage('ru')
  })
})
