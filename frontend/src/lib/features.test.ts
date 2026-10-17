import { describe, expect, it } from 'vitest'
import { hasRelay, normalizeFeatures, showsMetric, usesPin } from './features'

describe('hornet features', () => {
  it('a thermometer-only hornet asks for no relay or I2C pins', () => {
    expect(usesPin(['thermometer'], 'onewire')).toBe(true)
    expect(usesPin(['thermometer'], 'relay')).toBe(false)
    expect(usesPin(['thermometer'], 'sda')).toBe(false)
    expect(usesPin(['thermometer'], 'vbat')).toBe(true)
    expect(usesPin(null, 'relay')).toBe(true)
  })

  it('a heater implies a relay', () => {
    expect(normalizeFeatures(['heater', 'thermometer'])).toEqual(['thermometer', 'relay', 'heater'])
    expect(hasRelay(normalizeFeatures(['heater', 'thermometer']))).toBe(true)
    expect(hasRelay(['thermometer'])).toBe(false)
  })

  it('readings follow the sensors in use', () => {
    expect(showsMetric(['thermometer', 'relay'], 'temperature')).toBe(true)
    expect(showsMetric(['thermometer', 'relay'], 'humidity')).toBe(false)
    expect(showsMetric(['relay'], 'temperature')).toBe(false)
    expect(showsMetric(['relay'], 'battery_voltage')).toBe(true)
    expect(showsMetric(['thermometer'], 'humidity', 'esp-01-dht11')).toBe(true)
    expect(showsMetric(null, 'pressure')).toBe(true)
  })
})
