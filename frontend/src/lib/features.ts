/**
 * What a hornet is used for (config "features", backend DeviceFeatures): thermometer (DS18B20s on 1-Wire or a
 * DHT11/DHT22, which one is chosen with the pins), bme280 (I2C), relay, heater (the relay heats and the hornet keeps
 * its own limits). The device page shows only the
 * pins, readings and commands of the chosen features. null means the role has no features: everything is shown.
 */
export const allFeatures = ['thermometer', 'bme280', 'relay', 'heater'] as const
export type Feature = (typeof allFeatures)[number]

/** Sensors the thermometer can read (config hw.thermometer). */
export const thermometerSensors = ['ds18b20', 'dht11', 'dht22'] as const

/** Pin functions and the features that use them; vbat belongs to none and is always shown. */
const pinOwners: Record<string, readonly Feature[]> = {
  onewire: ['thermometer'],
  dht: ['thermometer'],
  sda: ['bme280'],
  scl: ['bme280'],
  relay: ['relay'],
  button: ['relay'],
}

/** Whether a pin is used; with the thermometer's sensor known, 1-Wire goes with DS18B20s and the DHT pin with a DHT. */
export function usesPin(features: readonly string[] | null | undefined, fn: string, thermometer?: string | null): boolean {
  if (thermometer && fn === 'onewire' && thermometer !== 'ds18b20') return false
  if (thermometer && fn === 'dht' && thermometer === 'ds18b20') return false
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
 * Whether a reading belongs to what the hornet is used for. Temperatures come from the thermometer or the BME280,
 * humidity from the BME280 or a DHT thermometer (thermometer: the DeviceDto's sensor), pressure from the BME280;
 * anything else (battery...) always shows.
 */
export function showsMetric(features: readonly string[] | null | undefined, metric: string, thermometer?: string | null): boolean {
  if (!features) return true
  if (metric.startsWith('temperature')) return features.includes('thermometer') || features.includes('bme280')
  if (metric.startsWith('humidity')) return features.includes('bme280') || (features.includes('thermometer') && !!thermometer?.startsWith('dht'))
  if (metric === 'pressure') return features.includes('bme280')
  return true
}

export function hasRelay(features: readonly string[] | null | undefined): boolean {
  return !features || features.includes('relay')
}
