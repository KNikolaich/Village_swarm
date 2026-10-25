import { describe, expect, it } from 'vitest'
import { clampToScreen, pixelHeight, screenText, stackedPlaces, textAnchor } from './display'

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
