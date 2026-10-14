import { useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, FlaskConical, Plus, Trash2, X } from 'lucide-react'
import { useEffect, useState, type ReactNode } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router'
import { toast } from 'sonner'
import { api, type DryRunResult, type RuleAction, type RuleCondition, type RuleDefinition, type RuleTrigger } from '@/api/client'
import { useDevices, useLatestTelemetry, useMe, useRuleRuns, useRules } from '@/api/queries'
import { Button } from '@/components/ui/button'
import { Badge, Card, CardContent, CardHeader, CardTitle, Input, Select, Skeleton } from '@/components/ui/primitives'
import { eventTypeLabel, metricLabel, severityLabel, t } from '@/i18n'
import { formatValue, formatWhen } from '@/lib/format'
import {
  actionKinds, conditionKinds, describe, eventTypes, explainReason, levels, newAction, newCondition, newTrigger, ops, templates, triggerKinds,
} from '@/lib/rules'
import { cn } from '@/lib/utils'

const blank = (): RuleDefinition => ({ trigger: newTrigger('telemetry'), conditions: [], actions: [newAction('notify')], cooldownS: 600 })

/** The reason the hive gave for refusing a rule (a validation problem), or a generic one. */
function reasonOf(error: unknown): string {
  const errors = (error as { errors?: Record<string, string[]> } | undefined)?.errors
  return errors ? Object.values(errors).flat().join('; ') : t('errors.generic')
}

/** Rule builder (spec 6.6: a form, not a JSON editor): trigger, conditions, actions, a dry run and the journal. */
export function RuleEditorPage() {
  const { id: idParam } = useParams()
  const [search] = useSearchParams()
  const navigate = useNavigate()
  const qc = useQueryClient()
  const me = useMe()
  const rules = useRules()
  const devices = useDevices()
  const isAdmin = me.data?.roles.includes('admin') ?? false
  const id = idParam && idParam !== 'new' ? Number(idParam) : null
  const existing = id != null ? rules.data?.find((r) => r.id === id) : undefined

  const [name, setName] = useState('')
  const [enabled, setEnabled] = useState(true)
  const [rule, setRule] = useState<RuleDefinition | null>(null)
  const [busy, setBusy] = useState(false)

  // Start from the stored rule, a template, or blank, once the data to do so is there.
  useEffect(() => {
    if (rule) return
    if (id != null) {
      if (!existing) return
      setName(existing.name)
      setEnabled(existing.enabled)
      setRule(existing.definition)
      return
    }
    if (!devices.data) return
    const tpl = templates(devices.data).find((x) => x.key === search.get('template'))
    setName(tpl?.name ?? '')
    setRule(tpl?.definition ?? blank())
  }, [id, existing, devices.data, rule, search])

  const deviceName = (d?: string | null) => (d ? devices.data?.find((x) => x.deviceId === d)?.name ?? d : t('rules.chooseDevice'))

  if (!rule) return <Skeleton className="h-64" />

  async function save() {
    setBusy(true)
    const body = { name: name.trim(), enabled, definition: rule! }
    const result = id == null
      ? await api.POST('/api/rules', { body })
      : await api.PUT('/api/rules/{id}', { params: { path: { id } }, body })
    setBusy(false)
    if (result.error || !result.data) {
      toast.error(t('rules.rejected').replace('{reason}', reasonOf(result.error)))
      return
    }
    toast.success(t('rules.saved'))
    await qc.invalidateQueries({ queryKey: ['rules'] })
    if (id == null) navigate(`/rules/${result.data.id}`, { replace: true })
  }

  async function remove() {
    if (id == null || !window.confirm(t('rules.deleteConfirm').replace('{name}', name))) return
    await api.DELETE('/api/rules/{id}', { params: { path: { id } } })
    await qc.invalidateQueries({ queryKey: ['rules'] })
    navigate('/rules')
  }

  const setTrigger = (trigger: RuleTrigger) => setRule({ ...rule, trigger })
  const setCondition = (i: number, c: RuleCondition | null) =>
    setRule({ ...rule, conditions: c ? rule.conditions.map((x, k) => (k === i ? c : x)) : rule.conditions.filter((_, k) => k !== i) })
  const setAction = (i: number, a: RuleAction | null) =>
    setRule({ ...rule, actions: a ? rule.actions.map((x, k) => (k === i ? a : x)) : rule.actions.filter((_, k) => k !== i) })

  return (
    <div className="space-y-4">
      <Link to="/rules" className="flex items-center gap-1 text-sm text-amber-400"><ArrowLeft className="size-4" />{t('rules.back')}</Link>

      {id == null && (
        <div className="flex flex-wrap items-center gap-2 text-sm">
          <span className="text-neutral-400">{t('rules.templatesTitle')}:</span>
          {templates(devices.data ?? []).map((tpl) => (
            <Button key={tpl.key} size="sm" variant="outline" onClick={() => { setName(tpl.name); setRule(tpl.definition) }}>{tpl.name}</Button>
          ))}
          <Button size="sm" variant="ghost" onClick={() => { setName(''); setRule(blank()) }}>{t('rules.blank')}</Button>
        </div>
      )}

      <Card>
        <CardContent className="space-y-3">
          <div className="flex flex-wrap items-end gap-3">
            <Field label={t('rules.name')} className="min-w-48 flex-1"><Input value={name} onChange={(e) => setName(e.target.value)} /></Field>
            <label className="flex h-11 items-center gap-2 text-sm text-neutral-300">
              <input type="checkbox" className="size-4 accent-amber-400" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} />
              {t('rules.enabled')}
            </label>
          </div>
          <p className="rounded-lg bg-neutral-800/50 p-3 text-sm text-neutral-300">{describe(rule, deviceName)}</p>
        </CardContent>
      </Card>

      <Section title={t('rules.trigger')}>
        <TriggerEditor trigger={rule.trigger} onChange={setTrigger} />
      </Section>

      <Section title={t('rules.conditions')}>
        {rule.conditions.length === 0 && <p className="text-sm text-neutral-500">{t('rules.noConditions')}</p>}
        {rule.conditions.map((c, i) => (
          <Item key={i} onRemove={() => setCondition(i, null)}>
            <ConditionEditor condition={c} onChange={(x) => setCondition(i, x)} />
          </Item>
        ))}
        <AddButton label={t('rules.addCondition')} kinds={conditionKinds} kindLabel={(k) => t(`rules.kinds.condition.${k}`)}
          onAdd={(k) => setRule({ ...rule, conditions: [...rule.conditions, newCondition(k)] })} />
      </Section>

      <Section title={t('rules.actions')}>
        {rule.actions.map((a, i) => (
          <Item key={i} onRemove={rule.actions.length > 1 ? () => setAction(i, null) : undefined}>
            <ActionEditor action={a} onChange={(x) => setAction(i, x)} />
          </Item>
        ))}
        <AddButton label={t('rules.addAction')} kinds={actionKinds} kindLabel={(k) => t(`rules.kinds.action.${k}`)}
          onAdd={(k) => setRule({ ...rule, actions: [...rule.actions, newAction(k)] })} />
        <Field label={t('rules.cooldown')} className="max-w-56">
          <Input type="number" min={0} value={Math.round(rule.cooldownS / 60)} onChange={(e) => setRule({ ...rule, cooldownS: Math.max(0, Number(e.target.value) || 0) * 60 })} />
        </Field>
      </Section>

      {isAdmin && (
        <div className="flex flex-wrap gap-2">
          <Button onClick={() => void save()} disabled={busy || !name.trim()}>{t('rules.save')}</Button>
          {id != null && <Button variant="ghost" onClick={() => void remove()}><Trash2 />{t('rules.delete')}</Button>}
        </div>
      )}

      {isAdmin && <DryRun rule={rule} name={deviceName} />}
      {id != null && <Journal id={id} name={deviceName} />}
    </div>
  )
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <Card>
      <CardHeader><CardTitle>{title}</CardTitle></CardHeader>
      <CardContent className="space-y-3">{children}</CardContent>
    </Card>
  )
}

function Item({ children, onRemove }: { children: ReactNode; onRemove?: () => void }) {
  return (
    <div className="relative rounded-lg border border-neutral-800 p-3 pr-10">
      {children}
      {onRemove && (
        <Button variant="ghost" size="icon" className="absolute right-0 top-0" title={t('rules.remove')} onClick={onRemove}><X /></Button>
      )}
    </div>
  )
}

function AddButton({ label, kinds, kindLabel, onAdd }: { label: string; kinds: readonly string[]; kindLabel: (k: string) => string; onAdd: (k: string) => void }) {
  return (
    <div className="flex items-center gap-2">
      <Plus className="size-4 text-neutral-500" />
      <Select value="" onChange={(e) => e.target.value && onAdd(e.target.value)} aria-label={label}>
        <option value="">{label}</option>
        {kinds.map((k) => <option key={k} value={k}>{kindLabel(k)}</option>)}
      </Select>
    </div>
  )
}

function Field({ label, children, className }: { label: string; children: ReactNode; className?: string }) {
  return (
    <label className={cn('block space-y-1 text-xs text-neutral-400', className)}>
      <span>{label}</span>
      {children}
    </label>
  )
}

/** A number field where empty means "not set" (null). */
function NumberField({ label, value, onChange, step, className }: { label: string; value: number | null | undefined; onChange: (v: number | null) => void; step?: number; className?: string }) {
  return (
    <Field label={label} className={cn('w-36', className)}>
      <Input type="number" step={step ?? 'any'} value={value ?? ''} onChange={(e) => onChange(e.target.value === '' ? null : Number(e.target.value))} />
    </Field>
  )
}

function DeviceSelect({ value, onChange, types, optional }: { value?: string | null; onChange: (v: string | null) => void; types?: string[]; optional?: boolean }) {
  const devices = useDevices()
  const list = (devices.data ?? []).filter((d) => !types || types.includes(d.type))
  return (
    <Field label={types?.includes('guard-cam') ? t('rules.camera') : t('rules.device')} className="min-w-44">
      <Select className="h-11 w-full" value={value ?? ''} onChange={(e) => onChange(e.target.value || null)}>
        <option value="">{optional ? t('rules.anyDevice') : t('rules.chooseDevice')}</option>
        {list.map((d) => <option key={d.deviceId} value={d.deviceId}>{d.name}</option>)}
        {value && !list.some((d) => d.deviceId === value) && <option value={value}>{value}</option>}
      </Select>
    </Field>
  )
}

function MetricSelect({ device, value, onChange }: { device?: string | null; value?: string | null; onChange: (v: string) => void }) {
  const latest = useLatestTelemetry()
  const known = [...new Set([...(latest.data ?? []).filter((v) => !device || v.deviceId === device).map((v) => v.metric), 'temperature', 'humidity', value ?? 'temperature'])]
  return (
    <Field label={t('rules.metric')} className="min-w-36">
      <Select className="h-11 w-full" value={value ?? ''} onChange={(e) => onChange(e.target.value)}>
        {known.map((m) => <option key={m} value={m}>{metricLabel(m)}</option>)}
      </Select>
    </Field>
  )
}

function DaysPicker({ value, onChange }: { value?: number[] | null; onChange: (v: number[] | null) => void }) {
  const days = value ?? []
  return (
    <Field label={t('rules.days')}>
      <div className="flex gap-1">
        {[1, 2, 3, 4, 5, 6, 7].map((d) => (
          <button
            key={d}
            type="button"
            onClick={() => {
              const next = days.includes(d) ? days.filter((x) => x !== d) : [...days, d].sort()
              onChange(next.length ? next : null)
            }}
            className={cn('h-9 w-10 rounded-lg border text-xs', days.includes(d) ? 'border-amber-400 bg-amber-400 text-neutral-950' : 'border-neutral-700 text-neutral-300')}
          >
            {t(`rules.days.${d}`)}
          </button>
        ))}
      </div>
    </Field>
  )
}

function KindSelect({ value, kinds, label, onChange }: { value: string; kinds: readonly string[]; label: (k: string) => string; onChange: (k: string) => void }) {
  return (
    <Select className="h-11 min-w-48" value={value} onChange={(e) => onChange(e.target.value)}>
      {kinds.map((k) => <option key={k} value={k}>{label(k)}</option>)}
    </Select>
  )
}

function TriggerEditor({ trigger: tr, onChange }: { trigger: RuleTrigger; onChange: (t: RuleTrigger) => void }) {
  const set = (patch: Partial<RuleTrigger>) => onChange({ ...tr, ...patch })
  return (
    <div className="flex flex-wrap items-end gap-3">
      <KindSelect value={tr.kind} kinds={triggerKinds} label={(k) => t(`rules.kinds.trigger.${k}`)} onChange={(k) => onChange(newTrigger(k))} />
      {tr.kind === 'telemetry' && (
        <>
          <DeviceSelect value={tr.device} onChange={(device) => set({ device })} />
          <MetricSelect device={tr.device} value={tr.metric} onChange={(metric) => set({ metric })} />
        </>
      )}
      {tr.kind === 'event' && (
        <>
          <Field label={t('rules.eventType')} className="min-w-44">
            <Select className="h-11 w-full" value={tr.eventType ?? ''} onChange={(e) => set({ eventType: e.target.value })}>
              {eventTypes.map((e) => <option key={e} value={e}>{eventTypeLabel(e)}</option>)}
            </Select>
          </Field>
          <DeviceSelect value={tr.device} onChange={(device) => set({ device })} optional />
        </>
      )}
      {tr.kind === 'schedule' && (
        <>
          <Field label={t('rules.at')} className="w-32"><Input type="time" value={tr.at ?? ''} onChange={(e) => set({ at: e.target.value })} /></Field>
          <DaysPicker value={tr.days} onChange={(days) => set({ days })} />
        </>
      )}
      {tr.kind === 'sun' && (
        <>
          <Field label={t('rules.sunWhat')} className="w-36">
            <Select className="h-11 w-full" value={tr.sun ?? 'sunset'} onChange={(e) => set({ sun: e.target.value })}>
              <option value="sunrise">{t('rules.sun.sunrise')}</option>
              <option value="sunset">{t('rules.sun.sunset')}</option>
            </Select>
          </Field>
          <NumberField label={t('rules.offset')} value={tr.offsetMin} step={1} onChange={(v) => set({ offsetMin: v ?? 0 })} className="w-52" />
        </>
      )}
      {tr.kind === 'offline' && (
        <>
          <DeviceSelect value={tr.device} onChange={(device) => set({ device })} />
          <NumberField label={t('rules.offlineMin')} value={tr.offlineMin} step={1} onChange={(v) => set({ offlineMin: v })} />
        </>
      )}
    </div>
  )
}

function ConditionEditor({ condition: c, onChange }: { condition: RuleCondition; onChange: (c: RuleCondition) => void }) {
  const set = (patch: Partial<RuleCondition>) => onChange({ ...c, ...patch })
  return (
    <div className="space-y-2">
      <div className="flex flex-wrap items-end gap-3">
        <KindSelect value={c.kind} kinds={conditionKinds} label={(k) => t(`rules.kinds.condition.${k}`)} onChange={(k) => onChange(newCondition(k))} />
        <label className="flex h-11 items-center gap-2 text-xs text-neutral-400">
          <input type="checkbox" className="size-4 accent-amber-400" checked={c.not} onChange={(e) => set({ not: e.target.checked })} />
          {t('rules.notLabel')}
        </label>
      </div>
      <div className="flex flex-wrap items-end gap-3">
        {c.kind === 'value' && (
          <>
            <DeviceSelect value={c.device} onChange={(device) => set({ device })} />
            <MetricSelect device={c.device} value={c.metric} onChange={(metric) => set({ metric })} />
            <Field label={t('rules.op')} className="w-24">
              <Select className="h-11 w-full" value={c.op ?? '<'} onChange={(e) => set({ op: e.target.value })}>
                {ops.map((o) => <option key={o} value={o}>{o}</option>)}
              </Select>
            </Field>
            <NumberField label={t('rules.value')} value={c.value} onChange={(value) => set({ value })} className="w-28" />
            <NumberField label={t('rules.forMin')} value={c.forS ? Math.round(c.forS / 60) : 0} step={1} onChange={(v) => set({ forS: (v ?? 0) * 60 })} className="w-32" />
            <NumberField label={t('rules.release')} value={c.release} onChange={(release) => set({ release })} className="w-44" />
          </>
        )}
        {c.kind === 'time' && (
          <>
            <Field label={t('rules.from')} className="w-32"><Input type="time" value={c.from ?? ''} onChange={(e) => set({ from: e.target.value })} /></Field>
            <Field label={t('rules.to')} className="w-32"><Input type="time" value={c.to ?? ''} onChange={(e) => set({ to: e.target.value })} /></Field>
            <DaysPicker value={c.days} onChange={(days) => set({ days })} />
          </>
        )}
        {c.kind === 'rain_past' && (
          <>
            <NumberField label={t('rules.mm')} value={c.mm} onChange={(mm) => set({ mm })} />
            <NumberField label={t('rules.hours')} value={c.hours} step={1} onChange={(hours) => set({ hours })} />
          </>
        )}
        {c.kind === 'rain_forecast' && (
          <>
            <NumberField label={t('rules.hoursAhead')} value={c.hours} step={1} onChange={(hours) => set({ hours })} />
            <NumberField label={t('rules.probability')} value={c.probability} step={1} onChange={(probability) => set({ probability })} className="w-44" />
            <NumberField label={t('rules.mm')} value={c.mm} onChange={(mm) => set({ mm })} />
          </>
        )}
        {c.kind === 'mode' && (
          <>
            <Field label={t('rules.mode')} className="w-36"><Input value={c.mode ?? ''} list="rule-modes" onChange={(e) => set({ mode: e.target.value })} /></Field>
            <OnOff label={t('rules.is')} value={c.is} onChange={(is) => set({ is })} values={['true', 'false']} />
            <datalist id="rule-modes"><option value="armed">{t('rules.modes.armed')}</option></datalist>
          </>
        )}
        {c.kind === 'device_state' && (
          <>
            <DeviceSelect value={c.device} onChange={(device) => set({ device })} types={['heat', 'relay', 'leak']} />
            <Field label={t('rules.channel')} className="w-32"><Input value={c.channel ?? ''} onChange={(e) => set({ channel: e.target.value })} /></Field>
            <OnOff label={t('rules.state')} value={c.is} onChange={(is) => set({ is })} values={['on', 'off']} />
          </>
        )}
      </div>
    </div>
  )
}

function OnOff({ label, value, values, onChange }: { label: string; value?: string | null; values: string[]; onChange: (v: string) => void }) {
  const word = (v: string) => (v === 'on' || v === 'true' ? t('rules.on') : t('rules.off'))
  return (
    <Field label={label} className="w-28">
      <Select className="h-11 w-full" value={value ?? values[0]} onChange={(e) => onChange(e.target.value)}>
        {values.map((v) => <option key={v} value={v}>{word(v)}</option>)}
      </Select>
    </Field>
  )
}

type Relay = { channel?: string; set?: string; for_s?: number }

function ActionEditor({ action: a, onChange }: { action: RuleAction; onChange: (a: RuleAction) => void }) {
  const set = (patch: Partial<RuleAction>) => onChange({ ...a, ...patch })
  const payload = (a.payload ?? {}) as Relay
  const mode = a.command === 'relay' && payload.set === 'on' ? 'on' : a.command === 'relay' && payload.set === 'off' ? 'off' : 'other'
  const [json, setJson] = useState(() => JSON.stringify(a.payload ?? {}))
  const [jsonError, setJsonError] = useState(false)
  const setPayload = (p: Relay) => set({ payload: p as never })

  return (
    <div className="space-y-2">
      <KindSelect value={a.kind} kinds={actionKinds} label={(k) => t(`rules.kinds.action.${k}`)} onChange={(k) => onChange(newAction(k))} />
      {a.kind === 'command' && (
        <div className="flex flex-wrap items-end gap-3">
          <DeviceSelect value={a.device} onChange={(device) => set({ device })} />
          <Field label={t('rules.command')} className="w-44">
            <Select className="h-11 w-full" value={mode} onChange={(e) => {
              const m = e.target.value
              if (m === 'other') set({ command: a.command === 'relay' ? 'reboot' : a.command, payload: null })
              else set({ command: 'relay', payload: { channel: payload.channel ?? 'heater', set: m, ...(m === 'on' && payload.for_s ? { for_s: payload.for_s } : {}) } as never })
            }}>
              <option value="on">{t('rules.relayOn')}</option>
              <option value="off">{t('rules.relayOff')}</option>
              <option value="other">{t('rules.otherCommand')}</option>
            </Select>
          </Field>
          {mode !== 'other' && (
            <>
              <Field label={t('rules.channel')} className="w-32"><Input value={payload.channel ?? ''} onChange={(e) => setPayload({ ...payload, channel: e.target.value })} /></Field>
              {mode === 'on' && (
                <NumberField label={t('rules.duration')} className="w-72" step={1}
                  value={payload.for_s ? Math.round(payload.for_s / 60) : null}
                  onChange={(v) => setPayload({ channel: payload.channel, set: 'on', ...(v ? { for_s: v * 60 } : {}) })} />
              )}
            </>
          )}
          {mode === 'other' && (
            <>
              <Field label={t('rules.commandName')} className="w-40"><Input value={a.command ?? ''} onChange={(e) => set({ command: e.target.value })} /></Field>
              <Field label={t('rules.payload')} className="min-w-64 flex-1">
                <Input value={json} className={jsonError ? 'border-red-500' : undefined} onChange={(e) => {
                  setJson(e.target.value)
                  try {
                    const parsed = e.target.value.trim() === '' ? null : JSON.parse(e.target.value)
                    if (parsed !== null && (typeof parsed !== 'object' || Array.isArray(parsed))) throw new Error('object')
                    setJsonError(false)
                    set({ payload: parsed })
                  } catch {
                    setJsonError(true)
                  }
                }} />
              </Field>
              {jsonError && <p className="w-full text-xs text-red-400">{t('rules.badPayload')}</p>}
            </>
          )}
        </div>
      )}
      {a.kind === 'notify' && (
        <div className="flex flex-wrap items-end gap-3">
          <Field label={t('rules.text')} className="min-w-64 flex-1"><Input value={a.text ?? ''} onChange={(e) => set({ text: e.target.value })} /></Field>
          <Field label={t('rules.level')} className="w-36">
            <Select className="h-11 w-full" value={a.level ?? 'warn'} onChange={(e) => set({ level: e.target.value })}>
              {levels.map((l) => <option key={l} value={l}>{severityLabel(l)}</option>)}
            </Select>
          </Field>
        </div>
      )}
      {a.kind === 'set_mode' && (
        <div className="flex flex-wrap items-end gap-3">
          <Field label={t('rules.mode')} className="w-36"><Input value={a.mode ?? ''} list="rule-modes-action" onChange={(e) => set({ mode: e.target.value })} /></Field>
          <OnOff label={t('rules.is')} value={a.value} values={['true', 'false']} onChange={(value) => set({ value })} />
          <datalist id="rule-modes-action"><option value="armed">{t('rules.modes.armed')}</option></datalist>
        </div>
      )}
      {a.kind === 'snapshot' && <DeviceSelect value={a.device} onChange={(device) => set({ device })} types={['guard-cam']} />}
    </div>
  )
}

/** "What would it have done": replays the rule as it is in the form, without saving or sending anything. */
function DryRun({ rule, name }: { rule: RuleDefinition; name: (id?: string | null) => string }) {
  const [hours, setHours] = useState(24)
  const [result, setResult] = useState<DryRunResult | null>(null)
  const [busy, setBusy] = useState(false)

  async function run() {
    setBusy(true)
    const response = await api.POST('/api/rules/dry-run', { body: { definition: rule, hours } })
    setBusy(false)
    if (response.error || !response.data) {
      setResult(null)
      toast.error(t('rules.rejected').replace('{reason}', reasonOf(response.error)))
      return
    }
    setResult(response.data)
  }

  const fired = result?.hits.filter((h) => h.outcome === 'fired').length ?? 0
  return (
    <Card>
      <CardHeader>
        <CardTitle>{t('rules.dryRun')}</CardTitle>
        <div className="flex items-center gap-2">
          <Select value={hours} onChange={(e) => setHours(Number(e.target.value))} aria-label={t('rules.dryRunHours')}>
            {[6, 24, 72, 168].map((h) => <option key={h} value={h}>{h < 24 ? t('device.rangeHours').replace('{n}', String(h)) : t('device.rangeDays').replace('{n}', String(h / 24))}</option>)}
          </Select>
          <Button size="sm" variant="outline" disabled={busy} onClick={() => void run()}><FlaskConical />{t('rules.dryRun')}</Button>
        </div>
      </CardHeader>
      {result && (
        <CardContent className="space-y-2 text-sm">
          <p className="text-neutral-400">{t('rules.dryRunChecked').replace('{n}', String(result.checked)).replace('{hits}', String(fired))}</p>
          {result.hits.length === 0 && <p className="text-neutral-500">{t('rules.dryRunNone')}</p>}
          <ul className="max-h-72 space-y-1 overflow-y-auto">
            {result.hits.map((h, i) => (
              <li key={i} className="flex flex-wrap items-center gap-2">
                <span className="tabular-nums text-neutral-400">{formatWhen(h.ts)}</span>
                {h.outcome === 'fired'
                  ? <Badge tone="ok">{t('rules.outcome.fired')}</Badge>
                  : <Badge tone="alarm">{t('rules.dryRunNoData').replace('{reason}', explainReason(h.reason, name))}</Badge>}
                {h.value != null && <span className="tabular-nums">{formatValue(rule.trigger.metric ?? '', h.value)}</span>}
              </li>
            ))}
          </ul>
          {result.notes.length > 0 && <p className="text-xs text-neutral-500">{result.notes.map((n) => t(`rules.notes.${n}`, n)).join(' ')}</p>}
        </CardContent>
      )}
    </Card>
  )
}

function Journal({ id, name }: { id: number; name: (id?: string | null) => string }) {
  const runs = useRuleRuns(id)
  const rows = runs.data ?? []
  return (
    <Card>
      <CardHeader><CardTitle>{t('rules.journal')}</CardTitle></CardHeader>
      <CardContent>
        {runs.isLoading && <Skeleton className="h-16" />}
        {!runs.isLoading && rows.length === 0 && <p className="text-sm text-neutral-500">{t('rules.journalEmpty')}</p>}
        <ul className="space-y-1 text-sm">
          {rows.map((r) => {
            const details = r.details as { value?: number; reason?: string; actions?: { action: string; device?: string; result: string }[] } | null
            return (
              <li key={r.id} className="flex flex-wrap items-center gap-2">
                <span className="tabular-nums text-neutral-400">{formatWhen(r.ts)}</span>
                <Badge tone={r.outcome === 'fired' ? 'ok' : 'alarm'}>{t(`rules.outcome.${r.outcome}`, r.outcome)}</Badge>
                {details?.value != null && <span className="tabular-nums">{details.value}</span>}
                {details?.reason && <span className="text-neutral-500">{explainReason(details.reason, name)}</span>}
                {details?.actions?.map((a, i) => <span key={i} className="text-xs text-neutral-500">{a.action}{a.device ? ` ${a.device}` : ''}: {a.result}</span>)}
              </li>
            )
          })}
        </ul>
      </CardContent>
    </Card>
  )
}
