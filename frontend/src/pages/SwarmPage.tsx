import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowRightLeft, Check, CircleHelp, Cpu, Moon, Pencil, Pin, Plus, Trash2, Wifi, WifiOff, X } from 'lucide-react'
import { useMemo, useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import { toast } from 'sonner'
import { api, call, type DeviceDto, type Schemas } from '@/api/client'
import { keys, useDevices, useMe } from '@/api/queries'
import { Button } from '@/components/ui/button'
import { Badge, Card, CardContent, Empty, Input, Select, Skeleton } from '@/components/ui/primitives'
import { statusLabel, t } from '@/i18n'
import { formatAgo } from '@/lib/format'
import { CodeBox } from '@/pages/AddHornetPage'

/** "Problems first": offline, then weak signal, then the rest (spec 8.2 screen 5). */
function badness(d: DeviceDto): number {
  if (d.status === 'disabled') return 4
  if (d.status !== 'online') return 0
  if ((d.rssi ?? 0) < -80) return 1
  return 2
}

/** Battery intervals offered in the UI; the contract allows 60..86400 s. 0 = always awake. */
const sleepChoices = [0, 300, 600, 900, 1800, 3600]

/** A battery hornet between wake-ups: status stays online (clean disconnect), but it has not spoken lately. */
function asleep(d: DeviceDto): boolean {
  if (!d.sleepS || d.sleepMode !== 'deep' || d.status !== 'online' || !d.lastSeenAt) return false
  return Date.now() - new Date(d.lastSeenAt).getTime() > 90_000
}

/** Pin fields per role; an unknown type shows them all. The firmware uses only these (BoardPins.RoleFunctions). */
const pinFields = [['onewirePin', 'onewire'], ['sdaPin', 'sda'], ['sclPin', 'scl'], ['relayPin', 'relay'], ['buttonPin', 'button'], ['vbatPin', 'vbat']] as const
const roleFunctions: Record<string, readonly string[]> = {
  meteo: ['onewire', 'sda', 'scl', 'vbat'],
  relay: ['relay', 'button', 'vbat'],
}

/** ESP8266 boards: one ADC (A0 = GPIO17) and D-labels on the silkscreen. */
const esp8266Boards = ['nodemcu-v3', 'wemos-d1-mini', 'esp-01', 'esp-01-dht11']

/** Short help for a pin field: what it is for and, where the board is known, what to type. */
function pinHelp(fn: string, board: string | null | undefined): string {
  const base = t(`swarm.pinHelp.${fn}`)
  if (!board) return base
  const extra = esp8266Boards.includes(board) ? t(`swarm.pinHelp.${fn}Esp8266`, '') : board.startsWith('esp32c3') ? t(`swarm.pinHelp.${fn}Esp32c3`, '') : ''
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
export function PinsPanel({ deviceId, editable, board, type }: { deviceId: string; editable: boolean; board?: string | null; type?: string }) {
  const qc = useQueryClient()
  const pins = useQuery({ queryKey: ['devices', deviceId, 'pins'], queryFn: () => call(api.GET('/api/devices/{deviceId}/pins', { params: { path: { deviceId } } })) })
  const [draft, setDraft] = useState<Record<string, string>>({})
  const [help, setHelp] = useState<string | null>(null)
  const used = type ? roleFunctions[type] : undefined
  const keys = used ? pinFields.filter(([, fn]) => used.includes(fn)) : pinFields
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
    try {
      await call(api.PUT('/api/devices/{deviceId}/pins', { params: { path: { deviceId } }, body }))
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
                <td className="py-1 pr-2 tabular-nums">{r.function === 'vbat' && board && esp8266Boards.includes(board) ? 'A0' : `GPIO${r.pin}`}</td>
                <td className="py-1 text-neutral-500">
                  {r.source === 'config' ? t('swarm.pinsByHand') : t('swarm.pinsDefault')}{r.note ? ` · ${r.note}` : ''}
                  {r.function === 'vbat' ? ` · ×${ratio ?? 1}` : ''}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {editable && (
        <div className="grid grid-cols-3 gap-2">
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
          {help && <p className="col-span-3 whitespace-pre-line rounded-lg bg-neutral-900 p-2 text-neutral-300">{help === 'ratio' ? t('swarm.pinHelp.ratio') : pinHelp(help, board)}</p>}
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

/** Over-the-air update of one hornet: the builds the hive has for its role and board, the running one marked. */
function FirmwarePanel({ device, builds, onDone }: { device: DeviceDto; builds: Schemas['FirmwareBuildDto'][]; onDone: () => void }) {
  const [busy, setBusy] = useState(false)
  const env = `${device.type}-${device.hw ?? '?'}`
  const suitable = builds.filter((b) => b.role === device.type && b.board === device.hw)

  async function update(build: Schemas['FirmwareBuildDto']) {
    const risky = (device.hw ?? '').startsWith('esp-01')
    const text = t('swarm.updateConfirm').replace('{name}', device.name).replace('{from}', device.fw ?? '?').replace('{to}', build.version ?? '?')
    if (!window.confirm(risky ? `${text}\n\n${t('swarm.updateRisk')}` : text)) return
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
      {suitable.length === 0 && <p className="text-neutral-500">{t('swarm.fwNone').replace('{env}', env)}</p>}
      {suitable.map((b) => {
        const current = b.version === device.fw
        return (
          <div key={b.build} className="flex items-center justify-between gap-2 border-t border-neutral-800 pt-2">
            <div>
              <p className="font-medium">{b.build} · {b.version ?? '?'}</p>
              <p className="text-neutral-500">{t('swarm.fwBuilt').replace('{when}', formatAgo(b.built))}</p>
            </div>
            {current ? (
              <span className="text-neutral-500">{t('swarm.fwCurrent')}</span>
            ) : (
              <Button size="sm" disabled={busy || device.status !== 'online'} onClick={() => void update(b)}>{t('swarm.fwInstall')}</Button>
            )}
          </div>
        )
      })}
      {suitable.length > 0 && suitable.every((b) => b.version === device.fw) && <p className="text-neutral-500">{t('swarm.fwNoNewer')}</p>}
      {device.status !== 'online' && <p className="text-amber-400">{t('swarm.fwOffline')}</p>}
    </div>
  )
}

/** Screen 5 (spec 8.2): every hornet, sorted by trouble, with replace and remove. */
export function SwarmPage() {
  const qc = useQueryClient()
  const devices = useDevices()
  const me = useMe()
  const isAdmin = me.data?.roles.includes('admin') ?? false
  const canControl = isAdmin || (me.data?.roles.includes('member') ?? false)
  const [pinsOpen, setPinsOpen] = useState<string | null>(null)
  const [fwOpen, setFwOpen] = useState<string | null>(null)
  const firmware = useQuery({ queryKey: ['firmware'], queryFn: () => call(api.GET('/api/firmware')), enabled: isAdmin })
  const [replaceCode, setReplaceCode] = useState<{ deviceId: string; code: string; expiresAt: string } | null>(null)
  const list = useMemo(
    () => [...(devices.data ?? [])].sort((a, b) => badness(a) - badness(b) || a.deviceId.localeCompare(b.deviceId)),
    [devices.data],
  )

  async function replace(deviceId: string) {
    if (!window.confirm(t('swarm.replaceConfirm').replace('{id}', deviceId))) return
    const code = await call(api.POST('/api/devices/{deviceId}/replace', { params: { path: { deviceId } } }))
    setReplaceCode({ deviceId, code: code.code, expiresAt: code.expiresAt })
  }

  /** A build for this hornet's role and board that is not the version it runs now (null: nothing to offer). */
  function newerBuild(d: DeviceDto) {
    return (firmware.data ?? []).find((b) => b.role === d.type && b.board === d.hw && b.version && b.version !== d.fw) ?? null
  }

  async function setSleep(d: DeviceDto, body: { sleepS?: number; sleepMode?: 'deep' | 'light' }) {
    await call(api.PATCH('/api/devices/{deviceId}/config', { params: { path: { deviceId: d.deviceId } }, body }))
    toast.success(t('swarm.sleepSaved'))
    await qc.invalidateQueries({ queryKey: keys.devices })
  }

  async function remove(d: DeviceDto) {
    if (!window.confirm(t('swarm.removeConfirm').replace('{name}', d.name))) return
    await call(api.DELETE('/api/devices/{deviceId}', { params: { path: { deviceId: d.deviceId } } }))
    toast.success(t('swarm.removed'))
    await qc.invalidateQueries({ queryKey: keys.devices })
  }

  return (
    <div className="space-y-3">
      <div className="flex items-center justify-between">
        <h1 className="text-lg font-semibold">{t('swarm.title')}</h1>
        {isAdmin && (
          <Link to="/swarm/add">
            <Button tabIndex={-1}><Plus />{t('swarm.add')}</Button>
          </Link>
        )}
      </div>

      {replaceCode && (
        <Card className="border-amber-900/60">
          <CardContent className="space-y-2">
            <p className="text-sm">{t('swarm.replaceHint').replace('{id}', replaceCode.deviceId)}</p>
            <CodeBox code={replaceCode.code} expiresAt={replaceCode.expiresAt} />
          </CardContent>
        </Card>
      )}

      {devices.isLoading && <Skeleton className="h-40" />}
      {!devices.isLoading && list.length === 0 && <Empty>{t('overview.noDevices')}</Empty>}
      <div className="space-y-2">
        {list.map((d) => (
          <Card key={d.deviceId} className={d.status === 'disabled' ? 'opacity-50' : undefined}>
            <CardContent className="flex items-center justify-between gap-3 py-3">
              <div className="min-w-0">
                <DeviceName device={d} editable={isAdmin && d.status !== 'disabled'} />
                <p className="truncate text-xs text-neutral-500">
                  {d.deviceId} · {d.type} · {d.fw ?? '—'} · {formatAgo(d.lastSeenAt)}
                </p>
                {canControl && d.type === 'meteo' && d.status !== 'disabled' && (
                  <label className="mt-1 flex items-center gap-2 text-xs text-neutral-400" title={t('swarm.sleepHint')}>
                    <Moon className="size-3" />
                    {t('swarm.sleep')}
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
              <div className="flex shrink-0 items-center gap-2">
                {d.rssi != null && d.status === 'online' && (
                  <span className={`text-xs tabular-nums ${d.rssi < -80 ? 'text-amber-400' : 'text-neutral-500'}`}>{d.rssi} dBm</span>
                )}
                {asleep(d) ? (
                  <Badge tone="neutral"><Moon className="size-3" />{t('swarm.sleeping')}</Badge>
                ) : (
                  <Badge tone={d.status === 'online' ? 'ok' : d.status === 'disabled' || d.status === 'pending' ? 'neutral' : 'alarm'}>
                    {d.status === 'online' ? <Wifi className="size-3" /> : <WifiOff className="size-3" />}
                    {statusLabel(d.status)}
                  </Badge>
                )}
                {isAdmin && d.status !== 'disabled' && (
                  <Button variant="ghost" size="icon" title={newerBuild(d) ? t('swarm.updateAvailable') : t('swarm.update')}
                    className={newerBuild(d) ? 'text-amber-400' : undefined} onClick={() => setFwOpen(fwOpen === d.deviceId ? null : d.deviceId)}><Cpu /></Button>
                )}
                {d.status !== 'disabled' && (
                  <Button variant="ghost" size="icon" title={t('swarm.pins')} onClick={() => setPinsOpen(pinsOpen === d.deviceId ? null : d.deviceId)}><Pin /></Button>
                )}
                {isAdmin && d.status !== 'disabled' && (
                  <>
                    <Button variant="ghost" size="icon" title={t('swarm.replaceTitle')} onClick={() => void replace(d.deviceId)}><ArrowRightLeft /></Button>
                    <Button variant="ghost" size="icon" title={t('swarm.remove')} onClick={() => void remove(d)}><Trash2 /></Button>
                  </>
                )}
              </div>
            </CardContent>
            {fwOpen === d.deviceId && (
              <CardContent className="border-t border-neutral-800 pt-3">
                <FirmwarePanel device={d} builds={firmware.data ?? []} onDone={() => setFwOpen(null)} />
              </CardContent>
            )}
            {pinsOpen === d.deviceId && (
              <CardContent className="border-t border-neutral-800 pt-3">
                <PinsPanel deviceId={d.deviceId} editable={isAdmin} board={d.hw} type={d.type} />
              </CardContent>
            )}
          </Card>
        ))}
      </div>
    </div>
  )
}
