import { useQuery, useQueryClient } from '@tanstack/react-query'
import { CircleHelp } from 'lucide-react'
import { useState } from 'react'
import { toast } from 'sonner'
import { api, call } from '@/api/client'
import { useLatestTelemetry } from '@/api/queries'
import { Button } from '@/components/ui/button'
import { Input, Select, Skeleton } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { formatAgo, formatValue } from '@/lib/format'
import { ntcMetrics, thermometerSensorsFor, usesPin } from '@/lib/features'
import { calibratedOffset, ntcDefaults, ntcNumberFields, ntcSides, parseNtcNumber, type NtcSettings } from '@/lib/ntc'
import { cn } from '@/lib/utils'

/** Pin fields per role, narrowed by the hornet's features; an unknown type shows them all (BoardPins.RoleFunctions). */
const pinFields = [['onewirePin', 'onewire'], ['dhtPin', 'dht'], ['sdaPin', 'sda'], ['sclPin', 'scl'], ['relayPin', 'relay'], ['buttonPin', 'button'], ['vbatPin', 'vbat'], ['ntcPin', 'ntc']] as const
const roleFunctions: Record<string, readonly string[]> = {
  meteo: ['onewire', 'dht', 'sda', 'scl', 'vbat', 'ntc'],
  relay: ['relay', 'button', 'vbat'],
  heat: ['onewire', 'dht', 'sda', 'scl', 'relay', 'button', 'vbat'],
  'guard-cam': ['relay'],
  monitor: ['sda', 'scl', 'vbat', 'ntc'],
}

/** ESP8266 boards: one ADC (A0 = GPIO17) and D-labels on the silkscreen. */
const esp8266Boards = ['nodemcu-v3', 'wemos-d1-mini', 'esp-01', 'esp-01-dht11']

/** Short help for a pin field: what it is for and, where the board is known, what to type. */
function pinHelp(fn: string, board: string | null | undefined): string {
  const base = t(`swarm.pinHelp.${fn}`)
  if (!board) return base
  const extra = esp8266Boards.includes(board) ? t(`swarm.pinHelp.${fn}Esp8266`, '')
    : board.startsWith('esp32c3') ? t(`swarm.pinHelp.${fn}Esp32c3`, '')
    : board === 'esp32cam-aithinker' ? t(`swarm.pinHelp.${fn}Esp32cam`, '') : ''
  return extra ? `${base} ${extra}` : base
}

/** A "?" next to a field: hover shows the hint, a tap opens it under the field (phones have no hover). */
function HelpTip({ text, open, onToggle }: { text: string; open: boolean; onToggle: () => void }) {
  return (
    <button type="button" title={text} aria-label={t('swarm.pinHelpLabel')} aria-expanded={open} onClick={onToggle}
      className={`inline-flex align-middle ${open ? 'text-amber-400' : 'text-neutral-500 hover:text-neutral-300'}`}>
      <CircleHelp className="size-3.5" />
    </button>
  )
}

/** Pins of one hornet: what each function uses (board default or set by hand), conflicts shown, and editing. */
export function PinsPanel({ deviceId, editable, board, type, features }: { deviceId: string; editable: boolean; board?: string | null; type?: string; features?: readonly string[] | null }) {
  const qc = useQueryClient()
  const pins = useQuery({ queryKey: ['devices', deviceId, 'pins'], queryFn: () => call(api.GET('/api/devices/{deviceId}/pins', { params: { path: { deviceId } } })) })
  const latest = useLatestTelemetry()
  const [draft, setDraft] = useState<Record<string, string>>({})
  const [help, setHelp] = useState<string | null>(null)
  const [reference, setReference] = useState('')
  const used = type ? roleFunctions[type] : undefined
  // The thermometer's sensor (DS18B20 or DHT) decides between the 1-Wire and the DHT pin; '' in the draft = the board's.
  const showSensor = pins.data?.thermometer != null && (!features || features.includes('thermometer'))
  const sensor = draft.thermometer !== undefined ? draft.thermometer || null : (pins.data?.thermometer ?? null)
  const keys = pinFields.filter(([, fn]) => (!used || used.includes(fn)) && usesPin(features, fn, sensor))
  const showRatio = keys.some(([, fn]) => fn === 'vbat')
  // The thermistor's divider and calibration: shown once it has a pin (set or being typed) or is the thermometer (A0 by default).
  const ntcRow = pins.data?.pins.find((r) => r.function === 'ntc')
  const ntcThermometer = showSensor && sensor === 'ntc'
  const showNtc = keys.some(([, fn]) => fn === 'ntc') && (ntcThermometer || (draft.ntcPin !== undefined ? draft.ntcPin.trim() !== '' : ntcRow?.pin != null))
  const ntc: NtcSettings = pins.data?.ntc ?? {}
  const ntcReading = ntcMetrics(ntcThermometer ? 'ntc' : null)
    .map((m) => latest.data?.find((v) => v.deviceId === deviceId && v.metric === m))
    .find((v) => v !== undefined)

  function fitToReference() {
    const ref = Number(reference.trim().replace(',', '.').replace('−', '-'))
    if (!ntcReading || reference.trim() === '' || !Number.isFinite(ref)) {
      toast.error(t(ntcReading ? 'swarm.ntc.fitBadReference' : 'swarm.ntc.fitNoReading'))
      return
    }
    // The reading carries the offset saved now; a typed one not saved yet is not in it.
    const offset = calibratedOffset(ntc.offset, ntcReading.value, ref)
    setDraft({ ...draft, 'ntc.offset': String(offset) })
    toast.success(t('swarm.ntc.fitDone').replace('{offset}', String(offset)))
  }

  async function save() {
    // The request is the whole set of hand-set pins: fields left alone keep what is set now, an emptied one goes back to the default.
    const rows = pins.data?.pins ?? []
    const body: Record<string, number | null> = {}
    for (const [field, fn] of pinFields) {
      const value = draft[field]
      if (value === undefined) {
        const row = rows.find((r) => r.function === fn)
        body[field] = row?.source === 'config' ? (row.pin ?? null) : null
        continue
      }
      const text = value.trim()
      if (text !== '' && !/^\d+$/.test(text)) {
        toast.error(t('swarm.pinsNotNumber').replace('{field}', fn).replace('{value}', text))
        return
      }
      body[field] = text === '' ? null : Number(text)
    }
    const ratioText = draft.vbatRatio?.trim().replace(',', '.')
    if (ratioText === undefined) body.vbatRatio = pins.data?.vbatRatio ?? null
    else if (ratioText === '') body.vbatRatio = null
    else if (!/^\d+(\.\d+)?$/.test(ratioText) || Number(ratioText) <= 0 || Number(ratioText) > 100) {
      toast.error(t('swarm.ratioInvalid'))
      return
    } else body.vbatRatio = Number(ratioText)
    const thermometer = draft.thermometer !== undefined ? draft.thermometer || null : pins.data?.thermometerByHand ? (pins.data.thermometer ?? null) : null
    let ntcBody: NtcSettings | null = null
    if (body.ntcPin != null || thermometer === 'ntc') {
      ntcBody = { side: draft['ntc.side'] !== undefined ? draft['ntc.side'] || null : (ntc.side ?? null) }
      for (const f of ntcNumberFields) {
        const text = draft[`ntc.${f}`]
        if (text === undefined) {
          ntcBody[f] = ntc[f] ?? null
          continue
        }
        const v = parseNtcNumber(f, text)
        if (v === undefined) {
          toast.error(t('swarm.ntc.invalid').replace('{field}', t(`swarm.ntc.${f}`)))
          return
        }
        ntcBody[f] = v
      }
    }
    try {
      await call(api.PUT('/api/devices/{deviceId}/pins', { params: { path: { deviceId } }, body: { ...body, thermometer, ntc: ntcBody } }))
      setDraft({})
      toast.success(t('swarm.pinsSaved'))
      await qc.invalidateQueries({ queryKey: ['devices', deviceId, 'pins'] })
    } catch {
      toast.error(t('swarm.pinsRejected'))
    }
  }

  if (pins.isLoading) return <Skeleton className="h-16" />
  const rows = pins.data?.pins ?? []
  const ratio = pins.data?.vbatRatio
  return (
    <div className="space-y-2 text-xs">
      {pins.data?.conflicts.length ? <p className="text-red-400">{t('swarm.pinsConflict')}: {pins.data.conflicts.join('; ')}</p> : null}
      {rows.length === 0 && <p className="text-neutral-500">{t('swarm.pinsNone')}</p>}
      {rows.length > 0 && (
        <table className="w-full">
          <tbody>
            {rows.map((r) => (
              <tr key={r.function} className="border-t border-neutral-800">
                <td className="py-1 pr-2 text-neutral-400">{r.function}</td>
                <td className={cn('py-1 pr-2 tabular-nums', r.pin == null && 'text-amber-400')}>
                  {r.pin == null ? t('swarm.pinsUnset') : (r.function === 'vbat' || r.function === 'ntc') && board && esp8266Boards.includes(board) ? 'A0' : `GPIO${r.pin}`}
                </td>
                <td className="py-1 text-neutral-500">
                  {r.source === 'config' ? t('swarm.pinsByHand') : r.source === 'none' ? t('swarm.pinsUnsetHint') : t('swarm.pinsDefault')}{r.note ? ` · ${r.note}` : ''}
                  {r.function === 'vbat' ? ` · ×${ratio ?? 1}` : ''}
                  {r.function === 'ntc' ? ` · ${t(`swarm.ntc.side_${ntc.side ?? ntcDefaults.side}`)}, B ${ntc.beta ?? ntcDefaults.beta}${ntc.offset ? `, ${ntc.offset > 0 ? '+' : ''}${ntc.offset}°` : ''}` : ''}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {showSensor && !editable && <p className="text-neutral-500">{t('swarm.thermometer')}: {t(`swarm.sensor.${pins.data?.thermometer}`)}</p>}
      {editable && (
        <div className="grid grid-cols-3 gap-2">
          {showSensor && (
            <label className="col-span-3 space-y-1">
              <span className="flex items-center gap-1 text-neutral-500">
                {t('swarm.thermometer')}
                <HelpTip text={t('swarm.pinHelp.thermometer')} open={help === 'thermometer'} onToggle={() => setHelp(help === 'thermometer' ? null : 'thermometer')} />
              </span>
              <Select
                value={draft.thermometer ?? (pins.data?.thermometerByHand ? (pins.data.thermometer ?? '') : '')}
                onChange={(e) => setDraft({ ...draft, thermometer: e.target.value })}
                className="h-8 w-full text-xs"
              >
                <option value="">{t('swarm.sensorBoard').replace('{sensor}', t(`swarm.sensor.${pins.data?.thermometerByHand ? 'unknown' : pins.data?.thermometer}`))}</option>
                {thermometerSensorsFor(type).map((s) => <option key={s} value={s}>{t(`swarm.sensor.${s}`)}</option>)}
              </Select>
            </label>
          )}
          {keys.map(([field, label]) => (
            <label key={field} className="space-y-1">
              <span className="flex items-center gap-1 text-neutral-500">
                {label}
                <HelpTip text={pinHelp(label, board)} open={help === label} onToggle={() => setHelp(help === label ? null : label)} />
              </span>
              <Input
                inputMode="numeric"
                placeholder={rows.find((r) => r.function === label)?.pin?.toString() ?? '—'}
                value={draft[field] ?? ''}
                onChange={(e) => setDraft({ ...draft, [field]: e.target.value })}
                className="h-8 text-xs"
              />
            </label>
          ))}
          {showRatio && (
            <label className="space-y-1">
              <span className="flex items-center gap-1 text-neutral-500">
                {t('swarm.ratio')}
                <HelpTip text={t('swarm.pinHelp.ratio')} open={help === 'ratio'} onToggle={() => setHelp(help === 'ratio' ? null : 'ratio')} />
              </span>
              <Input
                inputMode="decimal"
                placeholder={ratio?.toString() ?? '1'}
                value={draft.vbatRatio ?? ''}
                onChange={(e) => setDraft({ ...draft, vbatRatio: e.target.value })}
                className="h-8 text-xs"
              />
            </label>
          )}
          {showNtc && (
            <div className="col-span-3 grid grid-cols-3 gap-2 rounded-lg border border-neutral-800 p-2">
              <p className="col-span-3 flex items-center gap-1 font-medium text-neutral-300">
                {t('swarm.ntc.title')}
                <HelpTip text={t('swarm.pinHelp.ntcSettings')} open={help === 'ntcSettings'} onToggle={() => setHelp(help === 'ntcSettings' ? null : 'ntcSettings')} />
              </p>
              <label className="col-span-3 space-y-1">
                <span className="text-neutral-500">{t('swarm.ntc.side')}</span>
                <Select
                  value={draft['ntc.side'] ?? ntc.side ?? ''}
                  onChange={(e) => setDraft({ ...draft, 'ntc.side': e.target.value })}
                  className="h-8 w-full text-xs"
                >
                  <option value="">{t('swarm.ntc.sideDefault')}</option>
                  {ntcSides.map((s) => <option key={s} value={s}>{t(`swarm.ntc.side_${s}`)}</option>)}
                </Select>
              </label>
              {ntcNumberFields.map((f) => (
                <label key={f} className="space-y-1">
                  <span className="text-neutral-500">{t(`swarm.ntc.${f}`)}</span>
                  <Input
                    inputMode="decimal"
                    placeholder={String(ntc[f] ?? ntcDefaults[f])}
                    value={draft[`ntc.${f}`] ?? ''}
                    onChange={(e) => setDraft({ ...draft, [`ntc.${f}`]: e.target.value })}
                    className="h-8 text-xs"
                  />
                </label>
              ))}
              <div className="col-span-3 space-y-1">
                <span className="text-neutral-500">
                  {ntcReading
                    ? t('swarm.ntc.fitNow').replace('{value}', formatValue('temperature_ntc', ntcReading.value)).replace('{ago}', formatAgo(ntcReading.ts))
                    : t('swarm.ntc.fitNoReading')}
                </span>
                <div className="flex gap-2">
                  <Input
                    inputMode="decimal"
                    placeholder={t('swarm.ntc.reference')}
                    value={reference}
                    onChange={(e) => setReference(e.target.value)}
                    className="h-8 text-xs"
                  />
                  <Button size="sm" variant="secondary" onClick={fitToReference} disabled={!ntcReading || reference.trim() === ''}>{t('swarm.ntc.fit')}</Button>
                </div>
              </div>
            </div>
          )}
          {help && <p className="col-span-3 whitespace-pre-line rounded-lg bg-neutral-900 p-2 text-neutral-300">{help === 'ratio' || help === 'thermometer' || help === 'ntcSettings' ? t(`swarm.pinHelp.${help}`) : pinHelp(help, board)}</p>}
          <Button size="sm" className="col-span-3" onClick={() => void save()} disabled={Object.keys(draft).length === 0}>{t('swarm.pinsSave')}</Button>
        </div>
      )}
    </div>
  )
}
