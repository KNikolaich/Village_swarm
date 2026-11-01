import type { Schemas } from '@/api/client'

/**
 * NTC thermistor behind a divider (config hw.ntc_*, firmware Thermistor.h, docs/ntc-thermistor.md): its settings as the
 * pins page edits them. A field left empty keeps the firmware default.
 */
export type NtcSettings = Schemas['NtcSettings']
export type NtcNumberField = 'rSeries' | 'rNominal' | 'beta' | 'vcc' | 'offset'

export const ntcNumberFields: readonly NtcNumberField[] = ['rSeries', 'rNominal', 'beta', 'vcc', 'offset']
export const ntcSides = ['high', 'low'] as const

/** What the firmware takes when the config says nothing. */
export const ntcDefaults: Record<NtcNumberField, number> & { side: string } = { side: 'high', rSeries: 10000, rNominal: 10000, beta: 3950, vcc: 3.3, offset: 0 }

/** The limits of contracts/schemas/config.schema.json (the hive checks them too). */
export const ntcLimits: Record<NtcNumberField, readonly [number, number]> = {
  rSeries: [100, 1_000_000],
  rNominal: [100, 1_000_000],
  beta: [1000, 10000],
  vcc: [1, 5.5],
  offset: [-20, 20],
}

/** A typed number: null for an empty field (the default), undefined when it is not a number or out of the limits. */
export function parseNtcNumber(field: NtcNumberField, text: string): number | null | undefined {
  const s = text.trim().replace(',', '.').replace('−', '-')
  if (s === '') return null
  if (!/^[-+]?\d+(\.\d+)?$/.test(s)) return undefined
  const v = Number(s)
  const [min, max] = ntcLimits[field]
  return v >= min && v <= max ? v : undefined
}

/**
 * The offset that makes the thermistor read what a reference thermometer next to it shows: the one set now plus the
 * difference, to a tenth of a degree. Wait until both have settled (a few minutes side by side).
 */
export function calibratedOffset(current: number | null | undefined, reading: number, reference: number): number {
  return Math.round(((current ?? 0) + reference - reading) * 10) / 10
}
