/** A car (contracts/mqtt-topics.md "Car"): what a held control sends, how often, at what speed. */

/** While a control is held the drive is repeated this often; the car stops 500 ms after the last one. */
export const driveRepeatMs = 200

export type CarDirection = 'forward' | 'back' | 'left' | 'right' | 'forwardLeft' | 'forwardRight'

export interface Drive {
  left: number
  right: number
}

export const stopDrive: Drive = { left: 0, right: 0 }

/** Wheel speeds for a control at a speed (1..100): turns spin on the spot, the forward diagonals bend the way. */
export function carDrive(direction: CarDirection, speed: number): Drive {
  const s = Math.round(Math.min(100, Math.max(1, speed)))
  const slow = Math.round(s * 0.35)
  switch (direction) {
    case 'forward': return { left: s, right: s }
    case 'back': return { left: -s, right: -s }
    case 'left': return { left: -s, right: s }
    case 'right': return { left: s, right: -s }
    case 'forwardLeft': return { left: slow, right: s }
    case 'forwardRight': return { left: s, right: slow }
  }
}

/** Arrow keys and WASD while the controls have the focus. */
export function keyDirection(key: string): CarDirection | null {
  switch (key) {
    case 'ArrowUp': case 'w': case 'W': case 'ц': case 'Ц': return 'forward'
    case 'ArrowDown': case 's': case 'S': case 'ы': case 'Ы': return 'back'
    case 'ArrowLeft': case 'a': case 'A': case 'ф': case 'Ф': return 'left'
    case 'ArrowRight': case 'd': case 'D': case 'в': case 'В': return 'right'
    default: return null
  }
}

export const defaultCarSpeed = 60
const speedKey = 'car.speed'

export function loadCarSpeed(storage: Pick<Storage, 'getItem'> | undefined = globalThis.localStorage): number {
  try {
    const value = Number(storage?.getItem(speedKey))
    return Number.isInteger(value) && value >= 10 && value <= 100 ? value : defaultCarSpeed
  } catch {
    return defaultCarSpeed
  }
}

export function saveCarSpeed(speed: number, storage: Pick<Storage, 'setItem'> | undefined = globalThis.localStorage) {
  try {
    storage?.setItem(speedKey, String(speed))
  } catch {
    // private window or storage full: just not remembered
  }
}
