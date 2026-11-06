import { describe, expect, it } from 'vitest'
import { binaryString, chipMatches, lineMeaning } from '@/lib/hornetSetup'

describe('hornet setup over USB', () => {
  it('reads what the firmware says on its console', () => {
    expect(lineMeaning('[hornet] monitor fw=0.4.0+a1b2c3d boot=1 reset=poweron')).toBe('booted')
    expect(lineMeaning('enrolled monitor-hall')).toBe('enrolled')
    expect(lineMeaning('enroll failed: http 410')).toBe('enrollFailed')
    expect(lineMeaning('[wifi] now on "home"')).toBeNull()
  })

  it('matches the chip esptool finds to the chip family of the build', () => {
    expect(chipMatches('ESP32-C3', 'ESP32-C3')).toBe(true)
    expect(chipMatches('ESP32-S3', 'ESP32-S3')).toBe(true)
    expect(chipMatches('ESP8266', 'ESP8266')).toBe(true)
    expect(chipMatches('ESP32', 'ESP32')).toBe(true)
    expect(chipMatches('ESP32C3', 'ESP32-C3')).toBe(true)
    expect(chipMatches('ESP32-C3', 'ESP32')).toBe(false)  // a C3 on the desk, a classic ESP32 build picked
    expect(chipMatches('ESP32', 'ESP32-C3')).toBe(false)
    expect(chipMatches('ESP8266', 'ESP32')).toBe(false)
  })

  it('hands esptool the bytes unchanged, large images included', () => {
    const bytes = new Uint8Array(70_000).map((_, i) => i % 256)
    const s = binaryString(bytes)
    expect(s.length).toBe(70_000)
    expect(s.charCodeAt(0)).toBe(0)
    expect(s.charCodeAt(69_999)).toBe(69_999 % 256)
  })
})
