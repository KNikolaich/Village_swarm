import { describe, expect, it } from 'vitest'
import { carDrive, defaultCarSpeed, driveRepeatMs, keyDirection, loadCarSpeed, saveCarSpeed } from './car'

describe('car controls', () => {
  it('turns a control into wheel speeds: turns spin on the spot, diagonals bend', () => {
    expect(carDrive('forward', 60)).toEqual({ left: 60, right: 60 })
    expect(carDrive('back', 60)).toEqual({ left: -60, right: -60 })
    expect(carDrive('left', 50)).toEqual({ left: -50, right: 50 })
    expect(carDrive('right', 50)).toEqual({ left: 50, right: -50 })
    expect(carDrive('forwardLeft', 100)).toEqual({ left: 35, right: 100 })
    expect(carDrive('forward', 250)).toEqual({ left: 100, right: 100 })
  })

  it('repeats a held control well within the car\'s 500 ms stop', () => {
    expect(driveRepeatMs).toBeLessThanOrEqual(500 / 2)
  })

  it('knows arrows and WASD, in either keyboard layout', () => {
    expect(keyDirection('ArrowUp')).toBe('forward')
    expect(keyDirection('ц')).toBe('forward')
    expect(keyDirection('d')).toBe('right')
    expect(keyDirection('Enter')).toBeNull()
  })

  it('remembers the speed per browser and survives closed storage', () => {
    const store = new Map<string, string>()
    const storage = { getItem: (k: string) => store.get(k) ?? null, setItem: (k: string, v: string) => void store.set(k, v) }
    expect(loadCarSpeed(storage)).toBe(defaultCarSpeed)
    saveCarSpeed(80, storage)
    expect(loadCarSpeed(storage)).toBe(80)
    expect(loadCarSpeed({ getItem: () => '5' })).toBe(defaultCarSpeed)
    expect(loadCarSpeed({ getItem: () => { throw new Error('denied') } })).toBe(defaultCarSpeed)
  })
})
