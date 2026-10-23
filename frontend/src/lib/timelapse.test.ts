import { describe, expect, it } from 'vitest'
import { intervalLabel, timelapseIntervals } from './timelapse'

describe('timelapse intervals', () => {
  it('offers 5 minutes to a day, as the hive accepts', () => {
    expect(timelapseIntervals[0]).toBe(5)
    expect(timelapseIntervals.at(-1)).toBe(1440)
    expect(timelapseIntervals).toContain(90)
  })

  it('names minutes below two hours and hours above', () => {
    expect(intervalLabel(5)).toBe('каждые 5 мин')
    expect(intervalLabel(90)).toBe('каждые 90 мин')
    expect(intervalLabel(60)).toBe('каждый час')
    expect(intervalLabel(180)).toBe('каждые 3 ч')
    expect(intervalLabel(1440)).toBe('раз в сутки')
  })
})
