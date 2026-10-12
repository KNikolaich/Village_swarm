import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowUpCircle, Moon, Pin, Plus, RefreshCw, Trash2, Wifi, WifiOff } from 'lucide-react'
import { useMemo, useState } from 'react'
import { Link } from 'react-router'
import { toast } from 'sonner'
import { api, call, type DeviceDto } from '@/api/client'
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

/** Pins of one hornet: what each function uses (board default or set by hand), conflicts shown, and editing. */
function PinsPanel({ deviceId, editable }: { deviceId: string; editable: boolean }) {
  const qc = useQueryClient()
  const pins = useQuery({ queryKey: ['devices', deviceId, 'pins'], queryFn: () => call(api.GET('/api/devices/{deviceId}/pins', { params: { path: { deviceId } } })) })
  const [draft, setDraft] = useState<Record<string, string>>({})
  const keys = [['onewirePin', 'onewire'], ['sdaPin', 'sda'], ['sclPin', 'scl'], ['relayPin', 'relay'], ['buttonPin', 'button'], ['vbatPin', 'vbat']] as const

  async function save() {
    const body: Record<string, number | null> = {}
    for (const [field] of keys) {
      const value = draft[field]
      if (value === undefined) continue
      body[field] = value === '' ? null : Number(value)
    }
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
                <td className="py-1 pr-2 tabular-nums">GPIO{r.pin}</td>
                <td className="py-1 text-neutral-500">{r.source === 'config' ? t('swarm.pinsByHand') : t('swarm.pinsDefault')}{r.note ? ` · ${r.note}` : ''}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {editable && (
        <div className="grid grid-cols-3 gap-2">
          {keys.map(([field, label]) => (
            <label key={field} className="space-y-1">
              <span className="text-neutral-500">{label}</span>
              <Input
                inputMode="numeric"
                placeholder={rows.find((r) => r.function === label)?.pin?.toString() ?? '—'}
                value={draft[field] ?? ''}
                onChange={(e) => setDraft({ ...draft, [field]: e.target.value })}
                className="h-8 text-xs"
              />
            </label>
          ))}
          <Button size="sm" className="col-span-3" onClick={() => void save()} disabled={Object.keys(draft).length === 0}>{t('swarm.pinsSave')}</Button>
        </div>
      )}
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
  const firmware = useQuery({ queryKey: ['firmware'], queryFn: () => call(api.GET('/api/firmware')), enabled: isAdmin })
  const [replaceCode, setReplaceCode] = useState<{ deviceId: string; code: string; expiresAt: string } | null>(null)
  const list = useMemo(
    () => [...(devices.data ?? [])].sort((a, b) => badness(a) - badness(b) || a.deviceId.localeCompare(b.deviceId)),
    [devices.data],
  )

  async function replace(deviceId: string) {
    const code = await call(api.POST('/api/devices/{deviceId}/replace', { params: { path: { deviceId } } }))
    setReplaceCode({ deviceId, code: code.code, expiresAt: code.expiresAt })
  }

  /** A build for this hornet's role and board that is not the version it runs now (null: nothing to offer). */
  function newerBuild(d: DeviceDto) {
    return (firmware.data ?? []).find((b) => b.role === d.type && b.board === d.hw && b.version && b.version !== d.fw) ?? null
  }

  async function update(d: DeviceDto) {
    const build = newerBuild(d)
    if (!build) return
    const risky = (d.hw ?? '').startsWith('esp-01')
    const text = t('swarm.updateConfirm').replace('{name}', d.name).replace('{from}', d.fw ?? '?').replace('{to}', build.version ?? '?')
    if (!window.confirm(risky ? `${text}

${t('swarm.updateRisk')}` : text)) return
    try {
      await call(api.POST('/api/devices/{deviceId}/ota', { params: { path: { deviceId: d.deviceId } }, body: { build: build.build } }))
      toast.success(t('swarm.updateStarted'))
    } catch {
      toast.error(t('swarm.updateRefused'))
    }
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
                <p className="truncate font-medium">{d.name}</p>
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
                {isAdmin && d.status === 'online' && newerBuild(d) && (
                  <Button variant="ghost" size="icon" title={t('swarm.update')} className="text-amber-400" onClick={() => void update(d)}><ArrowUpCircle /></Button>
                )}
                {d.status !== 'disabled' && (
                  <Button variant="ghost" size="icon" title={t('swarm.pins')} onClick={() => setPinsOpen(pinsOpen === d.deviceId ? null : d.deviceId)}><Pin /></Button>
                )}
                {isAdmin && d.status !== 'disabled' && (
                  <>
                    <Button variant="ghost" size="icon" title={t('swarm.replace')} onClick={() => void replace(d.deviceId)}><RefreshCw /></Button>
                    <Button variant="ghost" size="icon" title={t('swarm.remove')} onClick={() => void remove(d)}><Trash2 /></Button>
                  </>
                )}
              </div>
            </CardContent>
            {pinsOpen === d.deviceId && (
              <CardContent className="border-t border-neutral-800 pt-3">
                <PinsPanel deviceId={d.deviceId} editable={isAdmin} />
              </CardContent>
            )}
          </Card>
        ))}
      </div>
    </div>
  )
}
