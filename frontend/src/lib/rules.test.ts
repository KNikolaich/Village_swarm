import { describe as group, expect, it } from 'vitest'
import type { DeviceDto } from '@/api/client'
import { describe, newAction, newCondition, newTrigger, templates } from './rules'

const device = (deviceId: string, type: string) => ({ deviceId, type, name: deviceId }) as DeviceDto
const devices = [device('meteo-out1', 'meteo'), device('heat-living1', 'heat'), device('relay-garden', 'relay')]
const name = (id?: string | null) => id ?? '?'

group('templates', () => {
  it('fill in the hornets of the house', () => {
    const frost = templates(devices).find((x) => x.key === 'frost')!.definition
    expect(frost.trigger.device).toBe('meteo-out1')
    expect(frost.actions[0].device).toBe('heat-living1')
    const watering = templates(devices).find((x) => x.key === 'watering')!.definition
    expect(watering.conditions.map((c) => [c.kind, c.not])).toEqual([['rain_past', true], ['rain_forecast', true]])
  })

  it('leave the hornet empty when the house has none of that kind', () => {
    const lights = templates([]).find((x) => x.key === 'lights')!.definition
    expect(lights.actions[0].device).toBeNull()
  })
})

group('describe', () => {
  it('reads as when, if, then', () => {
    const text = describe(templates(devices)[0].definition, name)
    expect(text).toMatch(/meteo-out1/)
    expect(text).toMatch(/heat-living1/)
    expect(text.split(',').length).toBeGreaterThanOrEqual(3)
  })

  it('covers every kind without falling back to the raw kind name', () => {
    for (const kind of ['telemetry', 'event', 'schedule', 'sun', 'offline'])
      expect(describe({ trigger: newTrigger(kind), conditions: [], actions: [newAction('notify')], cooldownS: 0 }, name)).not.toMatch(new RegExp(`\\b${kind}\\b`))
    for (const kind of ['value', 'time', 'dark', 'rain_past', 'rain_forecast', 'mode', 'device_state'])
      expect(describe({ trigger: newTrigger('schedule'), conditions: [newCondition(kind)], actions: [newAction('notify')], cooldownS: 0 }, name)).not.toMatch(new RegExp(`\\b${kind}\\b`))
  })
})
