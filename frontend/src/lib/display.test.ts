import { describe, expect, it } from 'vitest'
import {
  backgroundSize, clampPx, clampToScreen, clockText, fontBadge, isOn, moved, nearestSize, pixelHeight, screenText, sizeForPx, stackedPlaces, stateText, switchChannels,
  textAnchor, anchorRange, batteryBox, bringBack, dragBattery, dragRow, outsideScreen, pointToScreen, rowExtent,
} from './display'

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

describe('states of a screen', () => {
  it('reads on and off the way the hive does', () => {
    expect(isOn('ON')).toBe(true)
    expect(isOn('off')).toBe(false)
    expect(isOn(true)).toBe(true)
    expect(isOn(0)).toBe(false)
    expect(isOn(3)).toBe(true)
    expect(isOn('strange')).toBeNull()
    expect(isOn(null)).toBeNull()
  })

  it('offers only the states that are a switch', () => {
    expect(switchChannels({ relay2: 'off', relay: 'on', mode: 'auto', level: 'high', valve: true })).toEqual(['relay', 'relay2', 'valve'])
    expect(switchChannels(null)).toEqual([])
  })

  it('writes a state in the words of the person or вкл and выкл', () => {
    expect(stateText(true, '', '')).toBe('вкл')
    expect(stateText(false, '', '')).toBe('выкл')
    expect(stateText(true, ' горит ', 'нет')).toBe('горит')
    expect(stateText(null, 'горит', 'нет')).toBe('--')
  })
})

describe('dragging over the preview', () => {
  const square = { width: 200, height: 200 }
  const landscape = { width: 416, height: 240 }  // a panel on its side: the screen the person lays out is the turned one

  it('maps the pointer to the screen whatever size the preview is shown at', () => {
    // the preview scaled down to 100 by 100 points at (10, 20) on the page: every point of the page is two screen points
    expect(pointToScreen(60, 70, { left: 10, top: 20, width: 100, height: 100 }, square)).toEqual({ x: 100, y: 100 })
    expect(pointToScreen(10, 20, { left: 10, top: 20, width: 100, height: 100 }, square)).toEqual({ x: 0, y: 0 })
    // shown larger than the screen (a 3x preview): three page points are one screen point, fractions kept for the rounding to do
    const p = pointToScreen(31.5, 15, { left: 0, top: 0, width: 600, height: 600 }, square)
    expect(p.x).toBeCloseTo(10.5)
    expect(p.y).toBeCloseTo(5)
    // a landscape screen: the two axes scale by their own sizes (the picture's aspect ratio is the screen's)
    expect(pointToScreen(208, 120, { left: 0, top: 0, width: 208, height: 120 }, landscape)).toEqual({ x: 416, y: 240 })
  })

  it('keeps the text on the screen by the edge its alignment anchors: left, middle, right', () => {
    const grab = { dx: 0, dy: 0 }
    // a text 60 wide: left: x 0..140; center: x 30..170; right: x 60..200
    expect(anchorRange('left', 60, 200)).toEqual({ min: 0, max: 140 })
    expect(anchorRange('center', 60, 200)).toEqual({ min: 30, max: 170 })
    expect(anchorRange('right', 60, 200)).toEqual({ min: 60, max: 200 })
    expect(dragRow({ x: -50, y: 10 }, grab, 'left', 60, 20, square)).toEqual({ x: 0, y: 10 })
    expect(dragRow({ x: 500, y: 10 }, grab, 'left', 60, 20, square)).toEqual({ x: 140, y: 10 })
    expect(dragRow({ x: 0, y: 10 }, grab, 'center', 60, 20, square)).toEqual({ x: 30, y: 10 })
    expect(dragRow({ x: 500, y: 10 }, grab, 'center', 60, 20, square)).toEqual({ x: 170, y: 10 })
    // the fault that hid a row: aligned right, dragged to the left edge it ended at x = 0, its text entirely off the screen
    expect(dragRow({ x: 0, y: 10 }, grab, 'right', 60, 20, square)).toEqual({ x: 60, y: 10 })
    expect(dragRow({ x: 500, y: 10 }, grab, 'right', 60, 20, square)).toEqual({ x: 200, y: 10 })  // the right edge may be the screen's edge
    // y keeps the whole height on the screen
    expect(dragRow({ x: 10, y: 500 }, grab, 'left', 60, 20, square).y).toBe(180)
    expect(dragRow({ x: 10, y: -5 }, grab, 'left', 60, 20, square).y).toBe(0)
  })

  it('holds the row where it was taken: the grab offset from its anchor stays, to whole points', () => {
    // taken 12.4 points right of its anchor, the pointer moves to 150.6: the anchor is at 138.2, rounded
    expect(dragRow({ x: 150.6, y: 40.5 }, { dx: 12.4, dy: 3 }, 'left', 40, 20, square)).toEqual({ x: 138, y: 38 })
    // the same for a text aligned right: the anchor is its right edge, the offset is measured from there
    expect(dragRow({ x: 150.6, y: 40.5 }, { dx: -12.4, dy: 3 }, 'right', 40, 20, square)).toEqual({ x: 163, y: 38 })
  })

  it('a text wider than the screen gets the nearest anchor, not a range that cannot be met', () => {
    expect(anchorRange('left', 300, 200)).toEqual({ min: 0, max: 0 })
    expect(anchorRange('center', 300, 200)).toEqual({ min: 100, max: 100 })
    expect(anchorRange('right', 300, 200)).toEqual({ min: 200, max: 200 })
  })

  it('finds a row drawn outside the screen and brings it back', () => {
    // the row of the screenshot: aligned right at x = 0, a text 40 wide: its left edge is at -40, none of it is seen
    const row = { x: 0, y: 40, align: 'right' as const }
    const out = rowExtent(row, 40, 12)
    expect(out).toEqual({ left: -40, right: 0, top: 40, bottom: 52 })
    expect(outsideScreen(out, square)).toBe(true)
    expect(bringBack(row, 40, 12, square)).toEqual({ x: 40, y: 40 })
    expect(outsideScreen(rowExtent({ ...row, ...bringBack(row, 40, 12, square) }, 40, 12), square)).toBe(false)
    // inside: not flagged; a point of rounding is not flagged; centered and left edges are measured their own ways
    expect(outsideScreen(rowExtent({ x: 100, y: 10, align: 'center' }, 60, 10), square)).toBe(false)
    expect(outsideScreen(rowExtent({ x: 20, y: 10, align: 'center' }, 60, 10), square)).toBe(true)  // its left edge is at -10
    expect(outsideScreen(rowExtent({ x: 150, y: 10, align: 'left' }, 60, 10), square)).toBe(true)
    expect(outsideScreen(rowExtent({ x: 140, y: 10, align: 'left' }, 60.5, 10), square)).toBe(false)
    expect(outsideScreen(rowExtent({ x: 10, y: 195, align: 'left' }, 20, 10), square)).toBe(true)  // under the screen
    expect(bringBack({ x: 10, y: 195, align: 'left' }, 20, 10, square)).toEqual({ x: 10, y: 190 })
  })

  it('works the same on a screen turned on its side', () => {
    expect(dragRow({ x: 500, y: 300 }, { dx: 0, dy: 0 }, 'right', 100, 30, landscape)).toEqual({ x: 416, y: 210 })
    expect(dragRow({ x: 0, y: 0 }, { dx: 0, dy: 0 }, 'right', 100, 30, landscape)).toEqual({ x: 100, y: 0 })
    const portrait = { width: 240, height: 416 }
    expect(dragRow({ x: 500, y: 500 }, { dx: 0, dy: 0 }, 'center', 100, 30, portrait)).toEqual({ x: 190, y: 386 })
  })
})

describe('the battery icon on the layout', () => {
  const square = { width: 200, height: 200 }
  const base = { enabled: true, x: null, y: null, scale: 1 }

  it('stands in the top right corner at the base size unless the person says', () => {
    expect(batteryBox(base, square)).toEqual({ left: 184, top: 4, width: 10, height: 18 })
    expect(batteryBox({ ...base, scale: 3 }, square)).toEqual({ left: 152, top: 4, width: 30, height: 54 })  // 200 - 6*3 - 30
    expect(batteryBox({ ...base, x: 20, y: 120, scale: 2 }, square)).toEqual({ left: 20, top: 120, width: 20, height: 36 })
    expect(batteryBox({ ...base, scale: 9 }, square).width).toBe(40)  // the largest is four times
    expect(batteryBox(base, { width: 240, height: 416 }).left).toBe(224)  // a turned screen: its own width
  })

  it('is dragged by the corner it was taken at and stays on the screen', () => {
    // taken 3 right and 5 below its corner, the pointer at (100.4, 60.2): the corner is at (97.4, 55.2)
    expect(dragBattery({ x: 100.4, y: 60.2 }, { dx: 3, dy: 5 }, base, square)).toEqual({ x: 97, y: 55 })
    expect(dragBattery({ x: -40, y: -40 }, { dx: 0, dy: 0 }, base, square)).toEqual({ x: 0, y: 0 })
    expect(dragBattery({ x: 900, y: 900 }, { dx: 0, dy: 0 }, base, square)).toEqual({ x: 190, y: 182 })   // 200-10, 200-18
    expect(dragBattery({ x: 900, y: 900 }, { dx: 0, dy: 0 }, { ...base, scale: 3 }, square)).toEqual({ x: 170, y: 146 })
  })
})
