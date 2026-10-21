import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowRightLeft, BatteryMedium, CloudDownload, Camera, Check, CircleHelp, Cpu, Droplets, Flame, Gauge, HardDrive, Lightbulb, Moon, Pencil, Plus, Terminal, Thermometer, Usb, Wifi, WifiOff, X, type LucideIcon } from 'lucide-react'
import { useMemo, useState, type FormEvent } from 'react'
import { Link, useNavigate } from 'react-router'
import { FlashButton } from '@/components/FlashButton'
import { OtaRolloutPanel } from '@/components/OtaRolloutPanel'
import { toast } from 'sonner'
import { api, call, type DeviceDto, type LatestValueDto, type Schemas } from '@/api/client'
import { keys, useDevices, useLatestTelemetry, useMe, useOtaRollout, useSendCommand, useZones } from '@/api/queries'
import { Button } from '@/components/ui/button'
import { Badge, Card, CardContent, Empty, Input, Select, Skeleton } from '@/components/ui/primitives'
import { metricLabel, statusLabel, t } from '@/i18n'
import { formatAgo, formatValue } from '@/lib/format'
import { thermometerSensors, usesPin } from '@/lib/features'
import { candidates, newerBuild as newerBuildFor } from '@/lib/otaRollout'
import { groupDevices, groupings, mainReadings, quickControls, type Grouping } from '@/lib/swarm'
import { cn } from '@/lib/utils'

/** Battery intervals offered in the UI; the contract allows 60..86400 s. 0 = always awake. */
const sleepChoices = [0, 300, 600, 900, 1800, 3600]

/** A battery hornet between wake-ups: status stays online (clean disconnect), but it has not spoken lately. */
function asleep(d: DeviceDto): boolean {
  if (!d.sleepS || d.sleepMode !== 'deep' || d.status !== 'online' || !d.lastSeenAt) return false
  return Date.now() - new Date(d.lastSeenAt).getTime() > 90_000
}

/** Pin fields per role, narrowed by the hornet's features; an unknown type shows them all (BoardPins.RoleFunctions). */
const pinFields = [['onewirePin', 'onewire'], ['dhtPin', 'dht'], ['sdaPin', 'sda'], ['sclPin', 'scl'], ['relayPin', 'relay'], ['buttonPin', 'button'], ['vbatPin', 'vbat']] as const
const roleFunctions: Record<string, readonly string[]> = {
  meteo: ['onewire', 'dht', 'sda', 'scl', 'vbat'],
  relay: ['relay', 'button', 'vbat'],
  heat: ['onewire', 'dht', 'sda', 'scl', 'relay', 'button', 'vbat'],
  'guard-cam': ['relay'],
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
  const [draft, setDraft] = useState<Record<string, string>>({})
  const [help, setHelp] = useState<string | null>(null)
  const used = type ? roleFunctions[type] : undefined
  // The thermometer's sensor (DS18B20 or DHT) decides between the 1-Wire and the DHT pin; '' in the draft = the board's.
  const showSensor = pins.data?.thermometer != null && (!features || features.includes('thermometer'))
  const sensor = draft.thermometer !== undefined ? draft.thermometer || null : (pins.data?.thermometer ?? null)
  const keys = pinFields.filter(([, fn]) => (!used || used.includes(fn)) && usesPin(features, fn, sensor))
  const showRatio = keys.some(([, fn]) => fn === 'vbat')

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
    try {
      await call(api.PUT('/api/devices/{deviceId}/pins', { params: { path: { deviceId } }, body: { ...body, thermometer } }))
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
                  {r.pin == null ? t('swarm.pinsUnset') : r.function === 'vbat' && board && esp8266Boards.includes(board) ? 'A0' : `GPIO${r.pin}`}
                </td>
                <td className="py-1 text-neutral-500">
                  {r.source === 'config' ? t('swarm.pinsByHand') : r.source === 'none' ? t('swarm.pinsUnsetHint') : t('swarm.pinsDefault')}{r.note ? ` · ${r.note}` : ''}
                  {r.function === 'vbat' ? ` · ×${ratio ?? 1}` : ''}
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
                {thermometerSensors.map((s) => <option key={s} value={s}>{t(`swarm.sensor.${s}`)}</option>)}
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
          {help && <p className="col-span-3 whitespace-pre-line rounded-lg bg-neutral-900 p-2 text-neutral-300">{help === 'ratio' || help === 'thermometer' ? t(`swarm.pinHelp.${help}`) : pinHelp(help, board)}</p>}
          <Button size="sm" className="col-span-3" onClick={() => void save()} disabled={Object.keys(draft).length === 0}>{t('swarm.pinsSave')}</Button>
        </div>
      )}
    </div>
  )
}

/** The hornet's name, with an inline editor for admins (a typo made when adding it is fixed here). */
function DeviceName({ device, editable }: { device: DeviceDto; editable: boolean }) {
  const qc = useQueryClient()
  const [draft, setDraft] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function save(e: FormEvent) {
    e.preventDefault()
    const name = (draft ?? '').trim()
    if (!name || name === device.name) {
      setDraft(null)
      return
    }
    setBusy(true)
    try {
      await call(api.PUT('/api/devices/{deviceId}/name', { params: { path: { deviceId: device.deviceId } }, body: { name } }))
      toast.success(t('swarm.renamed'))
      setDraft(null)
      await qc.invalidateQueries({ queryKey: keys.devices })
    } catch {
      toast.error(t('swarm.renameFailed'))
    } finally {
      setBusy(false)
    }
  }

  if (draft === null)
    return (
      <div className="flex min-w-0 items-center gap-1">
        <Link to={`/swarm/${encodeURIComponent(device.deviceId)}`} className="block truncate font-medium hover:text-amber-400">{device.name}</Link>
        {editable && (
          <Button variant="ghost" size="icon" className="size-7 shrink-0 text-neutral-500" title={t('swarm.rename')} aria-label={t('swarm.rename')} onClick={() => setDraft(device.name)}>
            <Pencil className="size-3.5" />
          </Button>
        )}
      </div>
    )

  return (
    <form onSubmit={save} className="flex items-center gap-1">
      <Input
        autoFocus
        value={draft}
        maxLength={128}
        onChange={(e) => setDraft(e.target.value)}
        onKeyDown={(e) => { if (e.key === 'Escape') setDraft(null) }}
        className="h-8 min-w-0"
        aria-label={t('swarm.rename')}
      />
      <Button type="submit" variant="ghost" size="icon" className="size-8 shrink-0 text-emerald-400" disabled={busy || !draft.trim()} title={t('common.save')} aria-label={t('common.save')}><Check /></Button>
      <Button type="button" variant="ghost" size="icon" className="size-8 shrink-0" onClick={() => setDraft(null)} title={t('common.cancel')} aria-label={t('common.cancel')}><X /></Button>
    </form>
  )
}

/** A meteo or relay hornet may move to the heat firmware of its board, which does all they do (OtaService.CanBecome). */
const canBecome = (type: string, role: string) => role === 'heat' && (type === 'meteo' || type === 'relay')

/** Over-the-air update of one hornet: the builds the hive has for its role and board, the running one marked. */
function FirmwarePanel({ device, builds, onDone }: { device: DeviceDto; builds: Schemas['FirmwareBuildDto'][]; onDone: () => void }) {
  const [busy, setBusy] = useState(false)
  const [usbOpen, setUsbOpen] = useState<string | null>(null)
  const env = `${device.type}-${device.hw ?? '?'}`
  const suitable = builds.filter((b) => b.board === device.hw && (b.role === device.type || canBecome(device.type, b.role)))

  async function update(build: Schemas['FirmwareBuildDto']) {
    const risky = (device.hw ?? '').startsWith('esp-01')
    const text = build.role !== device.type
      ? t('swarm.becomeConfirm').replace('{name}', device.name).replace('{type}', t(`deviceTypes.${build.role}`, build.role))
      : t('swarm.updateConfirm').replace('{name}', device.name).replace('{from}', device.fw ?? '?').replace('{to}', build.version ?? '?')
    const notes = [risky ? t('swarm.updateRisk') : '', build.signed ? '' : t('swarm.updateUnsigned')].filter(Boolean)
    if (!window.confirm(notes.length ? `${text}\n\n${notes.join('\n\n')}` : text)) return
    setBusy(true)
    const { response, error } = await api.POST('/api/devices/{deviceId}/ota', { params: { path: { deviceId: device.deviceId } }, body: { build: build.build } })
    setBusy(false)
    if (response.ok) {
      toast.success(t('swarm.updateStarted'))
      onDone()
      return
    }
    const reason = (error as { errors?: Record<string, string[]> } | undefined)?.errors?.ota?.[0]
    toast.error(reason ? `${t('swarm.updateRefused')}: ${reason}` : t('swarm.updateRefused'))
  }

  return (
    <div className="space-y-2 text-xs">
      <p className="text-neutral-400">{t('swarm.fwRunning').replace('{version}', device.fw ?? '?')}</p>
      {!suitable.some((b) => b.role === device.type) && <p className="text-neutral-500">{t('swarm.fwNone').replace('{env}', env)}</p>}
      {suitable.map((b) => {
        const current = b.version === device.fw
        return (
          <div key={b.build} className="flex items-center justify-between gap-2 border-t border-neutral-800 pt-2">
            <div>
              <p className="font-medium">{b.build} · {b.version ?? '?'}</p>
              {b.role !== device.type && <p className="text-amber-400">{t('swarm.fwBecome').replace('{type}', t(`deviceTypes.${b.role}`, b.role))}</p>}
              <p className="text-neutral-500">{t('swarm.fwBuilt').replace('{when}', formatAgo(b.built))}</p>
            </div>
            <div className="flex shrink-0 items-center gap-2">
              {current ? (
                <span className="text-neutral-500">{t('swarm.fwCurrent')}</span>
              ) : (
                <Button size="sm" disabled={busy || device.status !== 'online'} onClick={() => void update(b)}>{t('swarm.fwInstall')}</Button>
              )}
              <Button size="sm" variant="outline" onClick={() => setUsbOpen(usbOpen === b.build ? null : b.build)}><Usb />{t('swarm.fwUsb')}</Button>
            </div>
          </div>
        )
      })}
      {usbOpen && (
        <div className="space-y-2 rounded-lg border border-neutral-800 p-3">
          <p className="text-neutral-300">{t('swarm.fwUsbHint')}</p>
          <FlashButton build={usbOpen} />
        </div>
      )}
      {suitable.some((b) => b.role === device.type) && suitable.every((b) => b.role !== device.type || b.version === device.fw) && <p className="text-neutral-500">{t('swarm.fwNoNewer')}</p>}
      {device.status !== 'online' && <p className="text-amber-400">{t('swarm.fwOffline')}</p>}
    </div>
  )
}

const readingIcons: Record<string, LucideIcon> = {
  temperature: Thermometer,
  temperature_2: Thermometer,
  humidity: Droplets,
  pressure: Gauge,
  battery_voltage: BatteryMedium,
  cpu_temperature: Cpu,
  disk_used_pct: HardDrive,
}

/** A reading older than this is shown dimmed: the hornet has not confirmed it lately. */
const staleAfterMs = 3 * 3600_000

/** The hornet's main readings as large numbers; the label and age are in the tooltip. */
function Readings({ device, latest }: { device: DeviceDto; latest: LatestValueDto[] }) {
  const readings = mainReadings(device, latest)
  if (readings.length === 0) return null
  return (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-1">
      {readings.map((v) => {
        const Icon = readingIcons[v.metric]
        const stale = device.status !== 'online' || Date.now() - new Date(v.ts).getTime() > staleAfterMs
        return (
          <span key={v.metric} title={`${metricLabel(v.metric)} · ${formatAgo(v.ts)}`}
            className={cn('flex items-center gap-1', stale && 'opacity-50')}>
            {Icon && <Icon className="size-4 text-neutral-500" />}
            <span className={cn('text-xl font-semibold tabular-nums', v.metric.startsWith('temperature') && v.value < 0 && 'text-sky-300')}>
              {formatValue(v.metric, v.value)}
            </span>
          </span>
        )
      })}
    </div>
  )
}

/** One-press controls of what the hornet drives: a relay toggles (the bulb is lit while it is on), a camera takes a photo. */
function QuickControls({ device }: { device: DeviceDto }) {
  const qc = useQueryClient()
  const send = useSendCommand(device.deviceId)
  const { relays, snapshot } = quickControls(device)
  if (relays.length === 0 && !snapshot) return null

  async function toggle(channel: string, on: boolean | null) {
    await send('relay', { channel, set: on ? 'off' : 'on' })
    // The new state comes back as the hornet's retained state; read it once it had time to arrive.
    for (const ms of [1500, 5000]) setTimeout(() => void qc.invalidateQueries({ queryKey: keys.devices }), ms)
  }

  return (
    <div className="flex items-center gap-1">
      {relays.map(({ channel, on }) => {
        const Icon = channel === 'heater' ? Flame : Lightbulb
        const title = t(on ? 'swarm.relayOnTitle' : on === false ? 'swarm.relayOffTitle' : 'swarm.relayUnknownTitle').replace('{channel}', channel)
        return (
          <Button key={channel} variant="outline" size="icon" title={title} aria-label={title} aria-pressed={on ?? undefined}
            className={on ? 'border-amber-400/60 bg-amber-400/15 text-amber-300 hover:bg-amber-400/25' : 'text-neutral-400'}
            onClick={() => void toggle(channel, on)}>
            <Icon className={on ? 'fill-amber-300/40' : undefined} />
          </Button>
        )
      })}
      {snapshot && (
        <Button variant="outline" size="icon" title={t('swarm.snapshot')} aria-label={t('swarm.snapshot')} onClick={() => void send('snapshot', { count: 1 })}>
          <Camera />
        </Button>
      )}
    </div>
  )
}

/** Screen 5 (spec 8.2): every hornet, grouped by zone or kind (hives first), with its readings and controls at hand. */
export function SwarmPage() {
  const qc = useQueryClient()
  const devices = useDevices(true)
  const me = useMe()
  const isAdmin = me.data?.roles.includes('admin') ?? false
  const canControl = isAdmin || (me.data?.roles.includes('member') ?? false)
  const [fwOpen, setFwOpen] = useState<string | null>(null)
  const firmware = useQuery({ queryKey: ['firmware'], queryFn: () => call(api.GET('/api/firmware')), enabled: isAdmin })
  const rollout = useOtaRollout(isAdmin)
  const rolloutActive = ['running', 'stopping'].includes(rollout.data?.rollout?.state ?? '')
  const [rolloutOpen, setRolloutOpen] = useState(false)
  // A rollout seen running keeps the panel open after it ends, so its result stays on screen.
  const [rolloutSeen, setRolloutSeen] = useState(false)
  if (rolloutActive && !rolloutSeen) {
    setRolloutSeen(true)
    setRolloutOpen(true)
  }
  const navigate = useNavigate()
  // Removed hornets are hidden unless asked for; remembered between visits.
  const [showDeceased, setShowDeceased] = useState(() => {
    try { return localStorage.getItem('swarm.showDeceased') === '1' } catch { return false }
  })
  function toggleDeceased(on: boolean) {
    setShowDeceased(on)
    try { localStorage.setItem('swarm.showDeceased', on ? '1' : '0') } catch { /* private window: just not remembered */ }
  }
  // How the list is grouped is remembered between visits too.
  const [grouping, setGrouping] = useState<Grouping>(() => {
    try {
      const saved = localStorage.getItem('swarm.grouping')
      return groupings.find((g) => g === saved) ?? 'zone'
    } catch { return 'zone' }
  })
  function changeGrouping(g: Grouping) {
    setGrouping(g)
    try { localStorage.setItem('swarm.grouping', g) } catch { /* private window: just not remembered */ }
  }
  const zones = useZones()
  const latest = useLatestTelemetry()
  const deceasedCount = (devices.data ?? []).filter((d) => d.status === 'disabled').length
  const outdated = useMemo(() => candidates(devices.data ?? [], firmware.data ?? []).length, [devices.data, firmware.data])
  const list = useMemo(() => (devices.data ?? []).filter((d) => showDeceased || d.status !== 'disabled'), [devices.data, showDeceased])
  const groups = useMemo(
    () => groupDevices(list, zones.data ?? [], grouping, {
      hives: t('swarm.groupHives'),
      noZone: t('swarm.noZone'),
      type: (type) => t(`deviceTypes.${type}`, type),
    }),
    [list, zones.data, grouping],
  )

  /** The wizard opens on the flashing step: the hornet's type and name are known, and the code is issued there. */
  function replace(deviceId: string, revive = false) {
    if ((devices.data ?? []).find((d) => d.deviceId === deviceId)?.driver === 'tasmota') {
      if (window.confirm(t('swarm.replaceTasmotaConfirm').replace('{id}', deviceId))) navigate(`/swarm/add?tasmota=${encodeURIComponent(deviceId)}`)
      return
    }
    if (!window.confirm(t(revive ? 'swarm.reviveConfirm' : 'swarm.replaceConfirm').replace('{id}', deviceId))) return
    navigate(`/swarm/add?replace=${encodeURIComponent(deviceId)}`)
  }

  /** A build for this hornet's role and board that is not the version it runs now (null: nothing to offer). */
  function newerBuild(d: DeviceDto) {
    return newerBuildFor(d, firmware.data ?? [])
  }

  async function setSleep(d: DeviceDto, body: { sleepS?: number; sleepMode?: 'deep' | 'light' }) {
    await call(api.PATCH('/api/devices/{deviceId}/config', { params: { path: { deviceId: d.deviceId } }, body }))
    toast.success(t('swarm.sleepSaved'))
    await qc.invalidateQueries({ queryKey: keys.devices })
  }

  return (
    <div className="space-y-3">
      <div className="flex items-center justify-between">
        <h1 className="text-lg font-semibold">{t('swarm.title')}</h1>
        {isAdmin && (
          <div className="flex flex-wrap items-center justify-end gap-2">
            {(outdated > 0 || rolloutActive) && (
              <Button variant="outline" className={rolloutActive ? 'text-amber-400' : undefined} onClick={() => setRolloutOpen(!rolloutOpen)}>
                <CloudDownload />{rolloutActive ? t('swarm.rollout.openRunning') : t('swarm.rollout.open').replace('{n}', String(outdated))}
              </Button>
            )}
            <Link to="/console">
              <Button variant="outline" tabIndex={-1}><Terminal />{t('swarm.console')}</Button>
            </Link>
            <Link to="/swarm/add">
              <Button tabIndex={-1}><Plus />{t('swarm.add')}</Button>
            </Link>
          </div>
        )}
      </div>

      {isAdmin && (rolloutOpen || rolloutActive) && (
        <OtaRolloutPanel devices={devices.data ?? []} builds={firmware.data ?? []} rollout={rollout.data?.rollout ?? null} onClose={() => setRolloutOpen(false)} />
      )}

      <div className="flex flex-wrap items-center justify-between gap-x-4 gap-y-2">
        <div className="flex items-center gap-1 rounded-lg bg-neutral-900 p-1 text-xs" role="radiogroup" aria-label={t('swarm.groupBy')}>
          {groupings.map((g) => (
            <button key={g} type="button" role="radio" aria-checked={grouping === g} onClick={() => changeGrouping(g)}
              className={cn('rounded-md px-3 py-1.5', grouping === g ? 'bg-neutral-700 text-neutral-100' : 'text-neutral-400 hover:text-neutral-200')}>
              {t(`swarm.group.${g}`)}
            </button>
          ))}
        </div>
        {deceasedCount > 0 && (
          <label className="flex cursor-pointer items-center gap-2 text-xs text-neutral-400" title={t('swarm.deceasedHint')}>
            <input type="checkbox" className="size-4 accent-amber-400" checked={showDeceased} onChange={(e) => toggleDeceased(e.target.checked)} />
            {t('swarm.showDeceased').replace('{n}', String(deceasedCount))}
          </label>
        )}
      </div>

      {devices.isLoading && <Skeleton className="h-40" />}
      {!devices.isLoading && list.length === 0 && <Empty>{t('overview.noDevices')}</Empty>}
      <div className="space-y-4">
        {groups.map((group) => (
          <section key={group.key} className="space-y-2">
            {group.title && <h2 className="px-1 text-xs font-medium uppercase tracking-wide text-neutral-500">{group.title}</h2>}
            {group.devices.map((d) => (
          <Card key={d.deviceId} className={d.status === 'disabled' ? 'opacity-50' : undefined}>
            <CardContent className="flex flex-wrap items-center gap-x-4 gap-y-2 py-3">
              <div className="min-w-0 flex-1 basis-48">
                <DeviceName device={d} editable={isAdmin && d.status !== 'disabled'} />
                <p className="truncate text-xs text-neutral-500">
                  {d.deviceId} · {d.type} · {d.driver === 'tasmota' ? 'Tasmota' : d.driver === 'zigbee' ? 'Zigbee' : (d.fw ?? '—')} · {formatAgo(d.lastSeenAt)}
                </p>
                {canControl && d.type === 'meteo' && d.status !== 'disabled' && (
                  <label className="mt-1 flex flex-wrap items-center gap-2 text-xs text-neutral-400" title={t('swarm.sleepHint')}>
                    <span className="flex items-center gap-2 whitespace-nowrap"><Moon className="size-3" />{t('swarm.sleep')}</span>
                    <Select
                      value={d.sleepS ?? 0}
                      onChange={(e) => void setSleep(d, { sleepS: Number(e.target.value) })}
                      className="h-7 text-xs"
                    >
                      {sleepChoices.map((s) => (
                        <option key={s} value={s}>{s === 0 ? t('swarm.sleepNever') : t('swarm.sleepMinutes').replace('{n}', String(s / 60))}</option>
                      ))}
                    </Select>
                    {!!d.sleepS && (
                      <Select
                        value={d.sleepMode}
                        onChange={(e) => void setSleep(d, { sleepMode: e.target.value as 'deep' | 'light' })}
                        className="h-7 text-xs"
                        aria-label={t('swarm.sleepMode')}
                      >
                        <option value="deep">{t('swarm.sleepDeep')}</option>
                        <option value="light">{t('swarm.sleepLight')}</option>
                      </Select>
                    )}
                  </label>
                )}
              </div>
              {d.status !== 'disabled' && <Readings device={d} latest={latest.data ?? []} />}
              <div className="ml-auto flex shrink-0 items-center gap-2">
                {canControl && d.status === 'online' && !asleep(d) && <QuickControls device={d} />}
                {d.rssi != null && d.status === 'online' && d.rssi < -80 && (
                  <span className="text-xs tabular-nums text-amber-400" title={t('swarm.weakSignal')}>{d.rssi} dBm</span>
                )}
                {asleep(d) ? (
                  <Badge tone="neutral"><Moon className="size-3" />{t('swarm.sleeping')}</Badge>
                ) : (
                  <Badge tone={d.status === 'online' ? 'ok' : d.status === 'disabled' || d.status === 'pending' ? 'neutral' : 'alarm'}>
                    {d.status === 'online' ? <Wifi className="size-3" /> : <WifiOff className="size-3" />}
                    {statusLabel(d.status)}
                  </Badge>
                )}
                {isAdmin && d.status !== 'disabled' && d.driver === 'hornet' && d.type !== 'hive' && (
                  <Button variant="ghost" size="icon" title={newerBuild(d) ? t('swarm.updateAvailable') : t('swarm.update')}
                    className={newerBuild(d) ? 'text-amber-400' : undefined} onClick={() => setFwOpen(fwOpen === d.deviceId ? null : d.deviceId)}><Cpu /></Button>
                )}
                {isAdmin && d.status === 'disabled' && d.driver !== 'zigbee' && (
                  <Button variant="outline" size="sm" title={t('swarm.reviveTitle')} onClick={() => replace(d.deviceId, true)}><ArrowRightLeft />{t('swarm.revive')}</Button>
                )}
                {isAdmin && d.status !== 'disabled' && d.type !== 'hive' && d.driver !== 'zigbee' && (
                  <Button variant="ghost" size="icon" title={t('swarm.replaceTitle')} onClick={() => replace(d.deviceId)}><ArrowRightLeft /></Button>
                )}
              </div>
            </CardContent>
            {fwOpen === d.deviceId && (
              <CardContent className="border-t border-neutral-800 pt-3">
                <FirmwarePanel device={d} builds={firmware.data ?? []} onDone={() => setFwOpen(null)} />
              </CardContent>
            )}
          </Card>
            ))}
          </section>
        ))}
      </div>
    </div>
  )
}
