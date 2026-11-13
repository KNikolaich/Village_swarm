/** The days picked in the photo calendar: press on the first day, release on the last. ISO dates compare as text. */
export interface DayRange {
  from: string
  to: string
}

/** Two days in any order become a range from the earlier to the later one (a single day: from === to). */
export const dayRange = (a: string, b: string): DayRange => (a <= b ? { from: a, to: b } : { from: b, to: a })

export const inDayRange = (iso: string, r: DayRange) => iso >= r.from && iso <= r.to

export const isSingleDay = (r: DayRange) => r.from === r.to

/** The range as the address keeps it: /photos/FROM?to=TO, and no ?to for one day. */
export const rangeFromUrl = (date: string, to: string | null): DayRange => (to && /^\d{4}-\d{2}-\d{2}$/.test(to) ? dayRange(date, to) : { from: date, to: date })
