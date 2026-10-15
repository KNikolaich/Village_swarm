import type { DeviceDto, RuleAction, RuleCondition, RuleDefinition, RuleTrigger } from '@/api/client'
import { eventTypeLabel, metricLabel, t } from '@/i18n'
import { formatValue } from '@/lib/format'

export const triggerKinds = ['telemetry', 'event', 'schedule', 'sun', 'offline'] as const
export const conditionKinds = ['value', 'time', 'dark', 'rain_past', 'rain_forecast', 'mode', 'device_state'] as const
export const actionKinds = ['command', 'notify', 'set_mode', 'snapshot'] as const
export const ops = ['<', '<=', '>', '>=', '==', '!='] as const
export const levels = ['info', 'warn', 'alarm', 'critical'] as const
/** Event types a hornet or the hive reports (contracts and HiveSensors); a rule can start on any of them. */
export const eventTypes = ['motion', 'door_open', 'leak', 'failsafe', 'tamper', 'photo_failed', 'overheat', 'disk_full', 'ota'] as const

export const newTrigger = (kind: string): RuleTrigger => {
  switch (kind) {
    case 'schedule': return { kind, at: '07:00', offsetMin: 0 }
    case 'sun': return { kind, sun: 'sunset', offsetMin: 0 }
    case 'offline': return { kind, offlineMin: 15, offsetMin: 0 }
    case 'event': return { kind, eventType: 'motion', offsetMin: 0 }
    default: return { kind: 'telemetry', metric: 'temperature', offsetMin: 0 }
  }
}

export const newCondition = (kind: string): RuleCondition => {
  switch (kind) {
    case 'time': return { kind, not: false, forS: 0, from: '22:00', to: '06:00' }
    case 'rain_past': return { kind, not: true, forS: 0, mm: 3, hours: 24 }
    case 'rain_forecast': return { kind, not: true, forS: 0, hours: 12, probability: 60 }
    case 'mode': return { kind, not: false, forS: 0, mode: 'armed', is: 'true' }
    case 'device_state': return { kind, not: false, forS: 0, channel: 'heater', is: 'on' }
    case 'dark': return { kind, not: false, forS: 0 }
    default: return { kind: 'value', not: false, forS: 600, metric: 'temperature', op: '<', value: 5 }
  }
}

export const newAction = (kind: string): RuleAction => {
  switch (kind) {
    case 'notify': return { kind, text: t('rules.defaults.notify'), level: 'warn' }
    case 'set_mode': return { kind, mode: 'armed', value: 'true' }
    case 'snapshot': return { kind }
    default: return { kind: 'command', command: 'relay', payload: { channel: 'heater', set: 'on', for_s: 3600 } as never }
  }
}

/** Ready-made starting points for the examples people ask for most. */
export function templates(devices: DeviceDto[]): { key: string; name: string; definition: RuleDefinition }[] {
  const sensor = devices.find((d) => d.type === 'meteo')?.deviceId ?? null
  const heater = devices.find((d) => d.type === 'heat')?.deviceId ?? null
  const relay = devices.find((d) => d.type === 'relay')?.deviceId ?? null
  return [
    {
      key: 'frost',
      name: t('rules.templates.frost'),
      definition: {
        trigger: { kind: 'telemetry', device: sensor, metric: 'temperature', offsetMin: 0 },
        conditions: [{ kind: 'value', not: false, device: sensor, metric: 'temperature', op: '<', value: 5, release: 7, forS: 600 }],
        actions: [
          { kind: 'command', device: heater, command: 'relay', payload: { channel: 'heater', set: 'on', for_s: 3600 } as never },
          { kind: 'notify', text: t('rules.defaults.frost'), level: 'warn' },
        ],
        cooldownS: 3000,
      },
    },
    {
      key: 'watering',
      name: t('rules.templates.watering'),
      definition: {
        trigger: { kind: 'schedule', at: '07:00', offsetMin: 0 },
        conditions: [
          { kind: 'rain_past', not: true, forS: 0, mm: 3, hours: 24 },
          { kind: 'rain_forecast', not: true, forS: 0, hours: 12, probability: 60 },
        ],
        actions: [{ kind: 'command', device: relay, command: 'relay', payload: { channel: 'relay', set: 'on', for_s: 1200 } as never }],
        cooldownS: 0,
      },
    },
    {
      key: 'cold',
      name: t('rules.templates.cold'),
      definition: {
        trigger: { kind: 'telemetry', device: sensor, metric: 'temperature', offsetMin: 0 },
        conditions: [{ kind: 'value', not: false, device: sensor, metric: 'temperature', op: '<', value: 3, release: 5, forS: 600 }],
        actions: [{ kind: 'notify', text: t('rules.defaults.cold'), level: 'critical' }],
        cooldownS: 3 * 3600,
      },
    },
    {
      key: 'armNight',
      name: t('rules.templates.armNight'),
      definition: {
        trigger: { kind: 'schedule', at: '23:00', offsetMin: 0 },
        conditions: [],
        actions: [{ kind: 'set_mode', mode: 'armed', value: 'true' }, { kind: 'notify', text: t('rules.defaults.armed'), level: 'info' }],
        cooldownS: 0,
      },
    },
    {
      key: 'disarmMorning',
      name: t('rules.templates.disarmMorning'),
      definition: {
        trigger: { kind: 'schedule', at: '07:00', offsetMin: 0 },
        conditions: [],
        actions: [{ kind: 'set_mode', mode: 'armed', value: 'false' }, { kind: 'notify', text: t('rules.defaults.disarmed'), level: 'info' }],
        cooldownS: 0,
      },
    },
    {
      key: 'lights',
      name: t('rules.templates.lights'),
      definition: {
        trigger: { kind: 'sun', sun: 'sunset', offsetMin: 0 },
        conditions: [],
        actions: [{ kind: 'command', device: relay, command: 'relay', payload: { channel: 'relay', set: 'on' } as never }],
        cooldownS: 0,
      },
    },
  ]
}

const dayNames = () => [1, 2, 3, 4, 5, 6, 7].map((d) => t(`rules.days.${d}`))
const daysText = (days?: number[] | null) => (days?.length ? ` (${days.map((d) => dayNames()[d - 1]).join(', ')})` : '')

/** One line in plain words: "when ..., if ..., then ...". `name` turns a device id into its name. */
export function describe(rule: RuleDefinition, name: (id?: string | null) => string): string {
  return [
    `${t('rules.when')} ${describeTrigger(rule.trigger, name)}`,
    rule.conditions.length ? `${t('rules.if')} ${rule.conditions.map((c) => describeCondition(c, name)).join(` ${t('rules.and')} `)}` : null,
    `${t('rules.then')} ${rule.actions.map((a) => describeAction(a, name)).join('; ')}`,
  ].filter(Boolean).join(', ')
}

export function describeTrigger(tr: RuleTrigger, name: (id?: string | null) => string): string {
  switch (tr.kind) {
    case 'telemetry': return t('rules.describe.telemetry').replace('{metric}', metricLabel(tr.metric ?? '')).replace('{device}', name(tr.device))
    case 'event': return t('rules.describe.event').replace('{type}', eventTypeLabel(tr.eventType ?? '')) + (tr.device ? ` (${name(tr.device)})` : '')
    case 'schedule': return t('rules.describe.schedule').replace('{at}', tr.at ?? '') + daysText(tr.days)
    case 'sun': {
      const what = t(`rules.sun.${tr.sun}`)
      if (!tr.offsetMin) return what
      return t(tr.offsetMin < 0 ? 'rules.describe.sunBefore' : 'rules.describe.sunAfter').replace('{n}', String(Math.abs(tr.offsetMin))).replace('{sun}', what)
    }
    case 'offline': return t('rules.describe.offline').replace('{device}', name(tr.device)).replace('{n}', String(tr.offlineMin ?? 0))
    default: return tr.kind
  }
}

export function describeCondition(c: RuleCondition, name: (id?: string | null) => string): string {
  const not = c.not ? `${t('rules.not')} ` : ''
  switch (c.kind) {
    case 'value': {
      let s = `${metricLabel(c.metric ?? '')} ${name(c.device)} ${c.op} ${formatValue(c.metric ?? '', c.value ?? 0)}`
      if (c.forS) s += ` ${t('rules.describe.for').replace('{n}', String(Math.round(c.forS / 60)))}`
      if (c.release != null) s += ` ${t('rules.describe.release').replace('{v}', formatValue(c.metric ?? '', c.release))}`
      return not + s
    }
    case 'time': return not + t('rules.describe.time').replace('{from}', c.from ?? '').replace('{to}', c.to ?? '') + daysText(c.days)
    case 'dark': return not + t('rules.describe.dark')
    case 'rain_past': return not + t('rules.describe.rainPast').replace('{mm}', String(c.mm ?? 1)).replace('{h}', String(c.hours ?? 24))
    case 'rain_forecast': return not + t('rules.describe.rainForecast').replace('{h}', String(c.hours ?? 12)).replace('{p}', String(c.probability ?? 60))
    case 'mode': return not + t('rules.describe.mode').replace('{mode}', t(`rules.modes.${c.mode}`, c.mode ?? '')).replace('{is}', c.is ?? '')
    case 'device_state': return not + t('rules.describe.state').replace('{device}', name(c.device)).replace('{channel}', c.channel ?? '').replace('{is}', c.is ?? '')
    default: return c.kind
  }
}

export function describeAction(a: RuleAction, name: (id?: string | null) => string): string {
  switch (a.kind) {
    case 'command': {
      const p = (a.payload ?? {}) as { set?: string; for_s?: number }
      if (a.command === 'relay' && p.set)
        return (p.set === 'on' ? t('rules.describe.relayOn') : t('rules.describe.relayOff')).replace('{device}', name(a.device))
          + (p.set === 'on' && p.for_s ? ` ${t('rules.describe.forMin').replace('{n}', String(Math.round(p.for_s / 60)))}` : '')
      return t('rules.describe.command').replace('{command}', a.command ?? '').replace('{device}', name(a.device))
    }
    case 'notify': return t('rules.describe.notify').replace('{level}', t(`severity.${a.level ?? 'warn'}`))
    case 'set_mode': return t('rules.describe.setMode').replace('{mode}', t(`rules.modes.${a.mode}`, a.mode ?? '')).replace('{value}', a.value ?? '')
    case 'snapshot': return t('rules.describe.snapshot').replace('{device}', name(a.device))
    default: return a.kind
  }
}

/** The engine's missing-data code (stale:{device}:{metric}, no_sun, ...) in words. */
export function explainReason(reason: string | null | undefined, name: (id?: string | null) => string): string {
  if (!reason) return ''
  const [code, device, metric] = reason.split(':')
  if (code === 'stale') return t('rules.reasons.stale').replace('{device}', name(device)).replace('{metric}', metricLabel(metric ?? ''))
  return t(`rules.reasons.${code}`, reason)
}
