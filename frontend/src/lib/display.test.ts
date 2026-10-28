import { describe, expect, it } from 'vitest'
import { backgroundSize, clampPx, clampToScreen, clockText, fontBadge, moved, nearestSize, pixelHeight, screenText, sizeForPx, stackedPlaces, textAnchor } from './display'

const oled = { width: 72, height: 40 }

describe('pixelHeight', () => {
  it('is a share of the screen height, never below the smallest font', () => {
    expect(pixelHeight(1, 40)).toBe(6)
    expect(pixelHeight(3, 40)).toBe(12)
    expect(pixelHeight(5, 40)).toBe(20)
    expect(pixelHeight(10, 40)).toBe(40)
    expect(pixelHeight(5, 480)).toBe(240)
    expect(pixelHeight(1, 200)).toBe(20)
    expect(pixelHeight(42, 40)).toBe(40)
  })
})

describe('screenText', () => {
  it('writes readings the way the hive does', () => {
    expect(screenText('temperature', -3.24)).toBe('-3.2°')
    expect(screenText('humidity', 63.4)).toBe('63%')
    expect(screenText('battery_voltage', 3.94)).toBe('3.9V')
    expect(screenText('pressure', 1013.4)).toBe('1013')
    expect(screenText('lux', null)).toBe('--')
  })
})

describe('placing', () => {
  it('keeps points on the screen', () => {
    expect(clampToScreen(-5, 99, oled)).toEqual({ x: 0, y: 39 })
    expect(clampToScreen(10.4, 20.6, oled)).toEqual({ x: 10, y: 21 })
  })

  it('stacks rows one under another when switching to placing by hand', () => {
    expect(stackedPlaces(2, oled)).toEqual([{ x: 0, y: 0, size: 5 }, { x: 0, y: 20, size: 5 }])
    expect(stackedPlaces(4, oled).map((p) => p.y)).toEqual([0, 10, 20, 30])
  })

  it('anchors the text by its alignment', () => {
    expect([textAnchor('left'), textAnchor('center'), textAnchor('right')]).toEqual(['start', 'middle', 'end'])
  })
})

describe('fontBadge', () => {
  it('says Digi for digits only, En without Russian letters and RU with them', () => {
    expect(fontBadge({ cyrillic: false, digitsOnly: true })).toBe('Digi')
    expect(fontBadge({ cyrillic: false, digitsOnly: false })).toBe('En')
    expect(fontBadge({ cyrillic: true, digitsOnly: false })).toBe('RU')
  })
})

describe('clampPx', () => {
  it('stays between 6 and 120 pixels', () => {
    expect(clampPx(3)).toBe(6)
    expect(clampPx(24.4)).toBe(24)
    expect(clampPx(500)).toBe(120)
    expect(clampPx(Number.NaN)).toBe(6)
  })
})

describe('backgroundSize', () => {
  it('names the exact size of the picture', () => {
    expect(backgroundSize({ width: 200, height: 200 })).toBe('200×200')
  })
})

describe('sizes of a font', () => {
  it('takes the offered size nearest to the old one', () => {
    expect(nearestSize(20, [8, 16, 24])).toBe(16)
    expect(nearestSize(22, [8, 16, 24])).toBe(24)
    expect(nearestSize(30, [])).toBe(30)
  })
  it('turns pixels into the relative size the layout keeps', () => {
    expect(sizeForPx(96, 240)).toBe(4)
    expect(sizeForPx(8, 240)).toBe(1)
    expect(sizeForPx(400, 240)).toBe(10)
  })
})

describe('clockText', () => {
  const at = new Date(2026, 9, 29, 9, 5)
  it('writes the clock the way the hive does', () => {
    expect(clockText('HH:mm', at)).toBe('09:05')
    expect(clockText('H:mm', at)).toBe('9:05')
    expect(clockText('d MMMM', at)).toBe('29 октября')
    expect(clockText('ddd, d MMM', at)).toBe('чт, 29 окт')
    expect(clockText('MMMM', at)).toBe('октябрь')
    expect(clockText('dd.MM.yyyy', at)).toBe('29.10.2026')
  })
})

describe('moved', () => {
  it('moves a row one place up or down and leaves the ends as they are', () => {
    expect(moved(['a', 'b', 'c'], 2, -1)).toEqual(['a', 'c', 'b'])
    expect(moved(['a', 'b', 'c'], 0, 1)).toEqual(['b', 'a', 'c'])
    const all = ['a', 'b']
    expect(moved(all, 0, -1)).toBe(all)
    expect(moved(all, 1, 1)).toBe(all)
  })
})
