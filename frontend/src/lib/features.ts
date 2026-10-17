/**
 * What a hornet is used for (config "features", backend DeviceFeatures): thermometer (DS18B20 on 1-Wire),
 * bme280 (I2C), relay, heater (the relay heats and the hornet keeps its own limits). The device page shows only the
 * pins, readings and commands of the chosen features. null means the role has no features: everything is shown.
 */
export const allFeatures = ['thermometer', 'bme280', 'relay', 'heater'] as const
export type Feature = (typeof allFeatures)[number]

/** Pin functions and the features that use them; vbat and dht belong to none and are always shown. */
const pinOwners: Record<string, readonly Feature[]> = {
  onewire: ['thermometer'],
  sda: ['bme280'],
  scl: ['bme280'],
  relay: ['relay'],
  button: ['relay'],
}

export function usesPin(features: readonly string[] | null | undefined, fn: string): boolean {
  const owners = pinOwners[fn]
  return !features || !owners || owners.some((f) => features.includes(f))
}

/** A heater switches the relay, so it implies one (the backend stores it the same way). */
export function normalizeFeatures(features: readonly string[]): Feature[] {
  const set = new Set(features)
  if (set.has('heater')) set.add('relay')
  return allFeatures.filter((f) => set.has(f))
}

/**
 * Whether a reading belongs to what the hornet is used for. Temperatures come from either sensor, humidity and
 * pressure from the BME280 (or the fixed DHT11 of the esp-01-dht11 board); anything else (battery...) always shows.
 */
export function showsMetric(features: readonly string[] | null | undefined, metric: string, board?: string | null): boolean {
  if (!features) return true
  if (metric.startsWith('temperature')) return features.includes('thermometer') || features.includes('bme280') || board === 'esp-01-dht11'
  if (metric === 'humidity') return features.includes('bme280') || board === 'esp-01-dht11'
  if (metric === 'pressure') return features.includes('bme280')
  return true
}

export function hasRelay(features: readonly string[] | null | undefined): boolean {
  return !features || features.includes('relay')
}
