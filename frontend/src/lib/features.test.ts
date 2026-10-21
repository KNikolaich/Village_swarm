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

  it('the thermometer uses 1-Wire for DS18B20s and the DHT pin for a DHT', () => {
    expect(usesPin(['thermometer'], 'onewire', 'ds18b20')).toBe(true)
    expect(usesPin(['thermometer'], 'dht', 'ds18b20')).toBe(false)
    expect(usesPin(['thermometer'], 'onewire', 'dht22')).toBe(false)
    expect(usesPin(['thermometer'], 'dht', 'dht22')).toBe(true)
    expect(usesPin(['relay'], 'dht', 'dht22')).toBe(false)
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
    expect(showsMetric(['thermometer'], 'humidity', 'dht22')).toBe(true)
    expect(showsMetric(['thermometer'], 'humidity', 'ds18b20')).toBe(false)
    expect(showsMetric([], 'temperature', 'dht11')).toBe(false)
    expect(showsMetric(null, 'pressure')).toBe(true)
  })
})
