import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Camera, CheckCircle2, Pencil, Plus, Search, Trash2 } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { Link, useNavigate } from 'react-router'
import { toast } from 'sonner'
import { api, call, type Schemas } from '@/api/client'
import { keys, useZones } from '@/api/queries'
import { PasswordInput } from '@/components/PasswordInput'
import { Button } from '@/components/ui/button'
import { Badge, Card, CardContent, CardHeader, CardTitle, Input, Select } from '@/components/ui/primitives'
import { getLanguage, t } from '@/i18n'

type Nvr = Schemas['NvrDto']
type Probe = Schemas['ProbeResultDto']

/** A channel on the form: whether it goes into the swarm, under which name and group. */
type Row = {
  channel: number
  take: boolean
  name: string
  zoneId: number | null
  recordWhenDisarmed: boolean
  snapshotSource: string
  snapshotQuality: string
  title?: string | null
  connected?: boolean | null
}

const snapshotSources = ['auto', 'cgi', 'rtsp']
const snapshotQualities = ['fast', 'detailed']

const clock = (iso?: string | null) => (iso ? new Date(iso).toLocaleTimeString(getLanguage() === 'en' ? 'en-GB' : 'ru-RU') : '—')

const nvrsKey = ['ipcams'] as const

const errorText = (code?: string | null) => t(`ipcam.errors.${code ?? 'unreachable'}`, t('errors.generic'))

function problem(err: unknown) {
  return (err as { errors?: Record<string, string[]> } | undefined)?.errors?.request?.[0] ?? t('errors.generic')
}

/**
 * "Add" → IP cameras: the wizard for a new Dahua NVR (contracts/ipcam.md). The hive talks to the NVR, so the form takes its
 * address and login, "Проверить" lists its channels, and every channel picked becomes a camera of the swarm. Once saved the
 * NVR's own page opens: that is where it lives (in the swarm, with its cameras under it).
 */
export function IpCamPanel() {
  const client = useQueryClient()
  const navigate = useNavigate()
  const [kind, setKind] = useState<'nvr' | 'camera'>('nvr')
  const done = (to: string) => {
    void client.invalidateQueries({ queryKey: nvrsKey })
    void client.invalidateQueries({ queryKey: keys.devices })
    navigate(`/swarm/${encodeURIComponent(to)}`)
  }
  return (
    <div className="space-y-4">
      <div className="flex flex-wrap gap-2">
        <Button variant={kind === 'nvr' ? 'default' : 'secondary'} size="sm" onClick={() => setKind('nvr')}>{t('ipcam.kindNvr')}</Button>
        <Button variant={kind === 'camera' ? 'default' : 'secondary'} size="sm" onClick={() => setKind('camera')}>{t('ipcam.kindCamera')}</Button>
      </div>
      <p className="text-sm text-neutral-400">{kind === 'nvr' ? t('ipcam.intro') : t('ipcam.cameraIntro')}</p>
      {kind === 'nvr'
        ? <NvrForm onDone={(saved) => done(saved.deviceId)} onCancel={() => navigate('/swarm')} />
        : <CameraForm onDone={(saved) => done(saved.channels[0]?.deviceId ?? saved.deviceId)} onCancel={() => navigate('/swarm')} />}
    </div>
  )
}

/** What "Проверить" says about a camera on its own: maker and model, and whether it tells motion or gives snapshots only. */
function CameraProbeLine({ probe }: { probe: Probe }) {
  if (!probe.ok) return <p className="text-red-400" title={probe.detail ?? undefined}>{errorText(probe.error)}</p>
  return (
    <div className="space-y-1">
      <p className="flex items-center gap-1 text-emerald-400">
        <CheckCircle2 className="size-4" />
        {t('ipcam.cameraFound').replace('{model}', probe.model || probe.deviceType || '?').replace('{vendor}', probe.vendor === 'dahua' ? 'Dahua' : 'ONVIF')}
      </p>
      {probe.motion === 'none' ? <p className="text-amber-300">{t('ipcam.motion.none')}</p> : <p className="text-xs text-neutral-500">{t(`ipcam.motion.${probe.motion}`)}</p>}
    </div>
  )
}

/**
 * A camera on its own (no NVR): address and login, "Проверить" finds out whether it is a Dahua or an ONVIF camera and where
 * its snapshots, streams and motion are; a stream address can be typed in when the camera does not give one. Becomes one
 * camera of the swarm with no parent. Editing (nvr given) keeps its kind and fills in what is known.
 */
function CameraForm({ nvr, onDone, onCancel }: { nvr?: Nvr; onDone: (saved: Nvr) => void; onCancel: () => void }) {
  const zones = useZones()
  const channel = nvr?.channels[0]
  const [name, setName] = useState(nvr?.name ?? '')
  const [host, setHost] = useState(nvr?.host ?? '')
  const [port, setPort] = useState(nvr?.port ?? 80)
  const [username, setUsername] = useState(nvr?.username ?? 'admin')
  const [password, setPassword] = useState('')
  const [rtsp, setRtsp] = useState(nvr?.rtspMain ?? '')
  const [zoneId, setZoneId] = useState<number | null>(channel?.zoneId ?? null)
  const [recordWhenDisarmed, setRecord] = useState(channel?.recordWhenDisarmed ?? false)
  const [probe, setProbe] = useState<Probe | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function check() {
    setBusy(true)
    setError(null)
    try {
      const result = nvr && !password
        ? await call(api.POST('/api/ipcams/{id}/probe', { params: { path: { id: nvr.id } } }))
        : await call(api.POST('/api/ipcams/probe', { body: { host, port, username, password, kind: 'camera' } }))
      setProbe(result)
      if (result.ok && result.rtspMain && !rtsp) setRtsp(result.rtspMain)
      if (result.ok && !name) setName(result.model || t('ipcam.camera'))
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  async function save(e: FormEvent) {
    e.preventDefault()
    setBusy(true)
    setError(null)
    const found = probe?.ok ? probe : null
    const body = {
      name,
      host,
      port,
      username,
      password: password || null,
      kind: 'camera',
      vendor: found?.vendor ?? nvr?.vendor ?? 'onvif',
      snapshotUrl: found ? found.snapshotUrl : nvr?.snapshotUrl,
      // A stream typed in wins over the one found (some cameras give ONVIF a wrong one).
      rtspMain: rtsp.trim() || null,
      rtspSub: found ? (rtsp.trim() && rtsp.trim() !== found.rtspMain ? null : found.rtspSub) : nvr?.rtspSub,
      eventsUrl: found ? found.eventsUrl : undefined,
      channels: [{
        channel: 1,
        name,
        zoneId,
        recordWhenDisarmed,
        snapshotSource: channel?.snapshotSource ?? 'auto',
        snapshotQuality: channel?.snapshotQuality ?? 'fast',
      }],
    }
    const { data: saved, error: err, response } = nvr
      ? await api.PUT('/api/ipcams/{id}', { params: { path: { id: nvr.id } }, body })
      : await api.POST('/api/ipcams', { body })
    setBusy(false)
    if (!response.ok) {
      setError(problem(err))
      return
    }
    toast.success(t('ipcam.saved'))
    if (saved) onDone(saved)
  }

  const dahua = (probe?.ok ? probe.vendor : nvr?.vendor) === 'dahua'
  const valid = name.trim() && host.trim() && username.trim() && (nvr || (password && probe?.ok))

  return (
    <Card>
      <CardHeader><CardTitle>{nvr ? nvr.name : t('ipcam.addCamera')}</CardTitle></CardHeader>
      <CardContent>
        <form onSubmit={save} className="space-y-3 text-sm">
          <div className="flex gap-2">
            <Input className="flex-1" placeholder={t('ipcam.cameraHost')} value={host} onChange={(e) => setHost(e.target.value)} />
            <Input className="w-24" type="number" min={1} max={65535} value={port} onChange={(e) => setPort(Number(e.target.value))} aria-label={t('ipcam.port')} />
          </div>
          <Input placeholder={t('ipcam.username')} value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="off" />
          <PasswordInput placeholder={nvr ? t('ipcam.passwordKeep') : t('ipcam.cameraPassword')} value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="new-password" />
          <p className="text-xs text-neutral-500">{t('ipcam.loginHint')}</p>
          <Button variant="secondary" onClick={() => void check()} disabled={busy || !host.trim() || !username.trim() || (!nvr && !password)}>
            <Search />{t('ipcam.probe')}
          </Button>
          {probe && <CameraProbeLine probe={probe} />}
          {(probe?.ok || nvr) && (
            <>
              <Input placeholder={t('add.namePlaceholder')} value={name} onChange={(e) => setName(e.target.value)} />
              {!dahua && (
                <label className="block space-y-1">
                  <span className="text-xs text-neutral-400">{t('ipcam.rtspUrl')}</span>
                  <Input placeholder="rtsp://192.168.1.50:554/stream1" value={rtsp} onChange={(e) => setRtsp(e.target.value)} />
                  <span className="block text-xs text-neutral-500">{t('ipcam.rtspUrlHint')}</span>
                </label>
              )}
              <div className="flex flex-wrap items-center gap-3">
                <Select className="h-10" value={zoneId ?? ''} onChange={(e) => setZoneId(e.target.value ? Number(e.target.value) : null)} aria-label={t('ipcam.zone')}>
                  <option value="">{t('ipcam.noZone')}</option>
                  {(zones.data ?? []).map((z) => <option key={z.id} value={z.id}>{z.name}</option>)}
                </Select>
                <label className="flex cursor-pointer items-center gap-2 text-xs text-neutral-400">
                  <input type="checkbox" className="size-4 accent-amber-400" checked={recordWhenDisarmed} onChange={(e) => setRecord(e.target.checked)} />
                  {t('ipcam.recordWhenDisarmed')}
                </label>
              </div>
            </>
          )}
          {error && <p className="text-sm text-red-400">{error}</p>}
          <div className="flex flex-wrap gap-2">
            <Button type="submit" disabled={busy || !valid}>{t('common.save')}</Button>
            <Button variant="ghost" onClick={onCancel}>{t('common.cancel')}</Button>
          </div>
          <p className="text-xs text-neutral-500">{t('ipcam.guardHint')}</p>
        </form>
      </CardContent>
    </Card>
  )
}

/** A camera on its own, on its page: how it is reached, whether it tells motion, check, edit, delete. */
function CameraConnection({ nvr }: { nvr: Nvr }) {
  const client = useQueryClient()
  const navigate = useNavigate()
  const [editing, setEditing] = useState(false)
  const [probe, setProbe] = useState<Probe | null>(null)
  const [busy, setBusy] = useState(false)

  async function check() {
    setBusy(true)
    try {
      setProbe(await call(api.POST('/api/ipcams/{id}/probe', { params: { path: { id: nvr.id } } })))
    } finally {
      setBusy(false)
    }
  }

  async function remove() {
    if (!window.confirm(t('ipcam.deleteCameraConfirm').replace('{name}', nvr.name))) return
    await call(api.DELETE('/api/ipcams/{id}', { params: { path: { id: nvr.id } } }))
    toast.success(t('ipcam.cameraDeleted'))
    void client.invalidateQueries({ queryKey: nvrsKey })
    void client.invalidateQueries({ queryKey: keys.devices })
    navigate('/swarm')
  }

  if (editing)
    return (
      <CameraForm
        nvr={nvr}
        onDone={() => {
          setEditing(false)
          void client.invalidateQueries({ queryKey: nvrsKey })
          void client.invalidateQueries({ queryKey: keys.devices })
        }}
        onCancel={() => setEditing(false)}
      />
    )

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t('ipcam.connection')}</CardTitle>
        <span className="text-xs text-neutral-500">{nvr.vendor === 'dahua' ? 'Dahua' : 'ONVIF'} · {nvr.host}{nvr.port !== 80 ? `:${nvr.port}` : ''}</span>
      </CardHeader>
      <CardContent className="space-y-2 text-sm">
        <LoginLine login={nvr.login} />
        {nvr.motion === 'none' ? <p className="text-amber-300">{t('ipcam.motion.none')}</p> : <StreamLine stream={nvr.stream} />}
        {probe && <CameraProbeLine probe={probe} />}
        <div className="flex flex-wrap gap-2">
          <Button variant="secondary" size="sm" onClick={() => void check()} disabled={busy}><Search />{t('ipcam.probe')}</Button>
          <Button variant="secondary" size="sm" onClick={() => setEditing(true)}><Pencil />{t('ipcam.edit')}</Button>
          <Button variant="ghost" size="sm" onClick={() => void remove()}><Trash2 />{t('ipcam.delete')}</Button>
        </div>
      </CardContent>
    </Card>
  )
}

const useNvrs = () => useQuery({ queryKey: nvrsKey, queryFn: () => call(api.GET('/api/ipcams')), refetchInterval: 5000 })

/**
 * The page of an NVR in the swarm (its device of type nvr): connection, event stream, every channel with its settings and a
 * snapshot, "Проверить", "Изменить", "Удалить". Settings are the administrator's.
 */
export function NvrDeviceView({ deviceId, isAdmin }: { deviceId: string; isAdmin: boolean }) {
  const client = useQueryClient()
  const navigate = useNavigate()
  const nvrs = useNvrs()
  const [editing, setEditing] = useState(false)
  const nvr = (nvrs.data ?? []).find((n) => n.deviceId === deviceId)

  if (!isAdmin) return <p className="text-sm text-neutral-500">{t('ipcam.adminOnly')}</p>
  if (nvrs.isLoading) return null
  if (!nvr) return <p className="text-sm text-neutral-500">{t('ipcam.nvrGone')}</p>

  async function remove(n: Nvr) {
    if (!window.confirm(t('ipcam.deleteConfirm').replace('{name}', n.name))) return
    await call(api.DELETE('/api/ipcams/{id}', { params: { path: { id: n.id } } }))
    toast.success(t('ipcam.deleted'))
    void client.invalidateQueries({ queryKey: nvrsKey })
    void client.invalidateQueries({ queryKey: keys.devices })
    navigate('/swarm')
  }

  return editing ? (
    <NvrForm
      nvr={nvr}
      onDone={() => {
        setEditing(false)
        void client.invalidateQueries({ queryKey: nvrsKey })
        void client.invalidateQueries({ queryKey: keys.devices })
      }}
      onCancel={() => setEditing(false)}
    />
  ) : (
    <NvrCard nvr={nvr} onEdit={() => setEditing(true)} onDelete={() => void remove(nvr)} />
  )
}

/**
 * The camera block on the page of one NVR channel: how its snapshots are taken (source, quality, photos when disarmed),
 * a snapshot now, what the NVR last said about it, and a way to its NVR. Name and group are edited above, like any device.
 */
export function IpCamChannelSettings({ deviceId, nvrId, isAdmin }: { deviceId: string; nvrId: number; isAdmin: boolean }) {
  const client = useQueryClient()
  const nvrs = useNvrs()
  const nvr = (nvrs.data ?? []).find((n) => n.id === nvrId)
  const channel = nvr?.channels.find((c) => c.deviceId === deviceId)
  const [busy, setBusy] = useState(false)
  if (!isAdmin || !nvr || !channel) return null

  async function save(patch: { snapshotSource?: string; snapshotQuality?: string; recordWhenDisarmed?: boolean }) {
    if (!nvr) return
    setBusy(true)
    const { error: err, response } = await api.PUT('/api/ipcams/{id}', {
      params: { path: { id: nvr.id } },
      body: {
        name: nvr.name,
        host: nvr.host,
        port: nvr.port,
        rtspPort: nvr.rtspPort,
        username: nvr.username,
        password: null,
        channels: nvr.channels.map((c) => ({
          channel: c.channel,
          name: c.name,
          zoneId: c.zoneId,
          recordWhenDisarmed: c.recordWhenDisarmed,
          snapshotSource: c.snapshotSource,
          snapshotQuality: c.snapshotQuality,
          ...(c.deviceId === deviceId ? patch : {}),
        })),
      },
    })
    setBusy(false)
    if (!response.ok) {
      toast.error(problem(err))
      return
    }
    toast.success(t('ipcam.saved'))
    void client.invalidateQueries({ queryKey: nvrsKey })
  }

  return (
    <>
    {nvr.kind === 'camera' && <CameraConnection nvr={nvr} />}
    <Card>
      <CardHeader>
        <CardTitle>{t('ipcam.cameraSettings')}</CardTitle>
        {nvr.kind !== 'camera' && (
          <Link to={`/swarm/${encodeURIComponent(nvr.deviceId)}`} className="text-xs text-amber-400">{nvr.name} · {t('ipcam.channel')} {channel.channel}</Link>
        )}
      </CardHeader>
      <CardContent className="space-y-3 text-sm">
        <div className="flex flex-wrap items-center gap-3">
          <label className="flex flex-wrap items-center gap-2 text-xs text-neutral-400">
            {t('ipcam.snapshotSource')}
            <Select className="h-9" value={channel.snapshotSource} disabled={busy} onChange={(e) => void save({ snapshotSource: e.target.value })}>
              {snapshotSources.map((s) => <option key={s} value={s}>{t(`ipcam.sources.${s}`)}</option>)}
            </Select>
          </label>
          <label className="flex flex-wrap items-center gap-2 text-xs text-neutral-400">
            {t('ipcam.snapshotQuality')}
            <Select className="h-9" value={channel.snapshotQuality} disabled={busy} onChange={(e) => void save({ snapshotQuality: e.target.value })}>
              {snapshotQualities.map((q) => <option key={q} value={q}>{t(`ipcam.qualities.${q}`)}</option>)}
            </Select>
          </label>
          <label className="flex cursor-pointer items-center gap-2 text-xs text-neutral-400">
            <input type="checkbox" className="size-4 accent-amber-400" checked={channel.recordWhenDisarmed} disabled={busy}
              onChange={(e) => void save({ recordWhenDisarmed: e.target.checked })} />
            {t('ipcam.recordWhenDisarmed')}
          </label>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          {channel.snapshotVia && <span className="text-xs text-neutral-500">{t('ipcam.via').replace('{via}', channel.snapshotVia)}</span>}
          <LiveSnapshotButton nvrId={nvr.id} channel={channel.channel} />
        </div>
        <ChannelDiagnostics channel={channel} />
      </CardContent>
    </Card>
    </>
  )
}

function NvrCard({ nvr, onEdit, onDelete }: { nvr: Nvr; onEdit: () => void; onDelete: () => void }) {
  const [probe, setProbe] = useState<Probe | null>(null)
  const [busy, setBusy] = useState(false)

  async function check() {
    setBusy(true)
    try {
      setProbe(await call(api.POST('/api/ipcams/{id}/probe', { params: { path: { id: nvr.id } } })))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>{nvr.name}</CardTitle>
        <span className="text-xs text-neutral-500">{nvr.host}{nvr.port !== 80 ? `:${nvr.port}` : ''}</span>
      </CardHeader>
      <CardContent className="space-y-2 text-sm">
        <ul className="space-y-2">
          {nvr.channels.map((c) => (
            <li key={c.channel} className="space-y-1">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <span className="min-w-0 truncate">{t('ipcam.channel')} {c.channel} · {c.name}</span>
                <span className="flex flex-wrap items-center justify-end gap-2">
                  {c.snapshotVia && <span className="text-xs text-neutral-500">{t('ipcam.via').replace('{via}', c.snapshotVia)}</span>}
                  <Badge tone={c.status === 'online' ? 'ok' : c.status === 'offline' ? 'alarm' : 'neutral'}>{t(`status.${c.status}`, c.status)}</Badge>
                  <LiveSnapshotButton nvrId={nvr.id} channel={c.channel} />
                </span>
              </div>
              <ChannelDiagnostics channel={c} />
            </li>
          ))}
        </ul>
        <LoginLine login={nvr.login} />
        <StreamLine stream={nvr.stream} />
        {probe && <ProbeLine probe={probe} />}
        <div className="flex flex-wrap gap-2">
          <Button variant="secondary" size="sm" onClick={() => void check()} disabled={busy}><Search />{t('ipcam.probe')}</Button>
          <Button variant="secondary" size="sm" onClick={onEdit}><Pencil />{t('ipcam.edit')}</Button>
          <Button variant="ghost" size="sm" onClick={onDelete}><Trash2 />{t('ipcam.delete')}</Button>
        </div>
      </CardContent>
    </Card>
  )
}

/**
 * "Снимок" next to a channel: a frame taken right now the way photos are taken (snapshot.cgi, then the RTSP stream),
 * or the NVR's own reason when there is none, so a camera that gives no photos can be told apart from one that gives no motion.
 */
function LiveSnapshotButton({ nvrId, channel }: { nvrId: number; channel: number }) {
  const [state, setState] = useState<{ url?: string; via?: string; error?: string } | null>(null)
  const [busy, setBusy] = useState(false)

  async function take() {
    setBusy(true)
    if (state?.url) URL.revokeObjectURL(state.url)
    try {
      const response = await fetch(`/api/ipcams/${nvrId}/snapshot?channel=${channel}`, { credentials: 'same-origin' })
      if (response.ok) {
        setState({ url: URL.createObjectURL(await response.blob()), via: response.headers.get('X-Snapshot-Via') ?? undefined })
      } else {
        const problem = (await response.json().catch(() => null)) as { detail?: string } | null
        setState({ error: problem?.detail ?? `HTTP ${response.status}` })
      }
    } catch (e) {
      setState({ error: String(e) })
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <Button variant="outline" size="sm" onClick={() => void take()} disabled={busy} title={t('ipcam.snapshot')}><Camera />{busy ? '…' : t('ipcam.snapshot')}</Button>
      {state && (
        <div className="basis-full space-y-1">
          {state.url && <img src={state.url} alt="" className="max-h-60 rounded-md" />}
          {state.via && <p className="text-xs text-neutral-500">{t('ipcam.via').replace('{via}', state.via)}</p>}
          {state.error && <p className="break-words text-xs text-red-400">{t('ipcam.snapshotFailed')}: {state.error}</p>}
        </div>
      )}
    </>
  )
}

/**
 * Why the hive is not talking to the NVR: it locked the login (Dahua does after a few wrong ones, for half an hour),
 * or refused it and the hive stopped trying so as not to cause that. Saving the login again resumes.
 */
function LoginLine({ login }: { login: Nvr['login'] }) {
  if (login.state === 'ok') return null
  const minutes = login.until ? Math.max(1, Math.ceil((new Date(login.until).getTime() - Date.now()) / 60_000)) : null
  const text = login.state === 'locked'
    ? t('ipcam.login.locked').replace('{n}', String(minutes ?? '?'))
    : login.state === 'paused'
      ? t('ipcam.login.paused')
      : t('ipcam.login.wait').replace('{n}', String(login.attemptsLeft ?? '?'))
  return <p className="rounded-md border border-red-900/60 bg-red-950/30 p-2 text-sm text-red-300">{text}</p>
}

/** The NVR's event stream: without it no motion comes, so this is the first thing to look at when there are no events. */
function StreamLine({ stream }: { stream: Nvr['stream'] }) {
  const text = stream.state === 'connected'
    ? t('ipcam.stream.connected').replace('{since}', clock(stream.since)).replace('{last}', clock(stream.lastLine)).replace('{n}', String(stream.reconnects))
    : t(`ipcam.stream.${stream.state}`, stream.state)
  return (
    <p className={`text-xs ${stream.state === 'connected' ? 'text-neutral-500' : 'text-amber-300'}`} title={stream.lastError ?? undefined}>
      {text}{stream.state !== 'connected' && stream.lastError ? `: ${stream.lastError}` : ''}
    </p>
  )
}

/** When the NVR last reported motion of the camera, and how the last snapshot went (way, size, time taken, or why not). */
function ChannelDiagnostics({ channel }: { channel: Nvr['channels'][number] }) {
  const s = channel.lastSnapshot
  const snapshot = !s
    ? null
    : s.ok
      ? t('ipcam.diag.snapshotOk').replace('{via}', s.via ?? '?').replace('{size}', s.size ?? '?').replace('{s}', (s.ms / 1000).toFixed(1)).replace('{at}', clock(s.at))
      : t('ipcam.diag.snapshotFailed').replace('{s}', (s.ms / 1000).toFixed(1)).replace('{at}', clock(s.at))
  return (
    <div className="space-y-0.5 text-xs text-neutral-500">
      <p>{t('ipcam.diag.motion').replace('{at}', channel.lastMotionAt ? clock(channel.lastMotionAt) : t('ipcam.diag.never'))}</p>
      {snapshot && <p className={s?.ok ? '' : 'text-red-400'}>{snapshot}</p>}
      {s?.error && <p className="break-words text-neutral-600">{s.error}</p>}
    </div>
  )
}

function ProbeLine({ probe }: { probe: Probe }) {
  return probe.ok ? (
    <p className="flex items-center gap-1 text-emerald-400"><CheckCircle2 className="size-4" />{t('ipcam.probeOk').replace('{type}', probe.deviceType ?? '').replace('{n}', String(probe.channels.length))}</p>
  ) : (
    <p className="text-red-400" title={probe.detail ?? undefined}>{errorText(probe.error)}</p>
  )
}

function NvrForm({ nvr, onDone, onCancel }: { nvr?: Nvr; onDone: (saved: Nvr) => void; onCancel: () => void }) {
  const zones = useZones()
  const [name, setName] = useState(nvr?.name ?? t('ipcam.defaultName'))
  const [host, setHost] = useState(nvr?.host ?? '')
  const [port, setPort] = useState(nvr?.port ?? 80)
  const [rtspPort, setRtspPort] = useState(nvr?.rtspPort ?? 554)
  const [username, setUsername] = useState(nvr?.username ?? 'admin')
  const [password, setPassword] = useState('')
  const [rows, setRows] = useState<Row[]>(
    (nvr?.channels ?? []).map((c) => ({ channel: c.channel, take: true, name: c.name, zoneId: c.zoneId ?? null, recordWhenDisarmed: c.recordWhenDisarmed, snapshotSource: c.snapshotSource, snapshotQuality: c.snapshotQuality })),
  )
  const [probe, setProbe] = useState<Probe | null>(null)
  const [extra, setExtra] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const set = (channel: number, patch: Partial<Row>) => setRows((all) => all.map((r) => (r.channel === channel ? { ...r, ...patch } : r)))

  /** Channels the NVR reported join the list (unticked unless already taken); the ones already there keep their names. */
  function merge(result: Probe) {
    setRows((all) => {
      const next = all.map((r) => {
        const c = result.channels.find((x) => x.channel === r.channel)
        return c ? { ...r, title: c.title, connected: c.connected } : r
      })
      for (const c of result.channels) {
        if (!next.some((r) => r.channel === c.channel)) next.push({ channel: c.channel, take: false, name: c.title || `${t('ipcam.camera')} ${c.channel}`, zoneId: null, recordWhenDisarmed: false, snapshotSource: 'auto', snapshotQuality: 'fast', title: c.title, connected: c.connected })
      }
      return next.sort((a, b) => a.channel - b.channel)
    })
  }

  async function check() {
    setBusy(true)
    setError(null)
    try {
      // Editing without retyping the password: the stored login is checked.
      const result = nvr && !password
        ? await call(api.POST('/api/ipcams/{id}/probe', { params: { path: { id: nvr.id } } }))
        : await call(api.POST('/api/ipcams/probe', { body: { host, port, username, password } }))
      setProbe(result)
      if (result.ok) merge(result)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  function addChannel() {
    const n = Number(extra)
    if (!Number.isInteger(n) || n < 1 || n > 256 || rows.some((r) => r.channel === n)) return
    setRows((all) => [...all, { channel: n, take: true, name: `${t('ipcam.camera')} ${n}`, zoneId: null, recordWhenDisarmed: false, snapshotSource: 'auto', snapshotQuality: 'fast' }].sort((a, b) => a.channel - b.channel))
    setExtra('')
  }

  async function save(e: FormEvent) {
    e.preventDefault()
    setBusy(true)
    setError(null)
    const body = {
      name,
      host,
      port,
      username,
      password: password || null,
      rtspPort,
      channels: rows.filter((r) => r.take).map((r) => ({ channel: r.channel, name: r.name, zoneId: r.zoneId, recordWhenDisarmed: r.recordWhenDisarmed, snapshotSource: r.snapshotSource, snapshotQuality: r.snapshotQuality })),
    }
    const { data: saved, error: err, response } = nvr
      ? await api.PUT('/api/ipcams/{id}', { params: { path: { id: nvr.id } }, body })
      : await api.POST('/api/ipcams', { body })
    setBusy(false)
    if (!response.ok) {
      setError(problem(err))
      return
    }
    toast.success(t('ipcam.saved'))
    if (saved) onDone(saved)
  }

  const taken = rows.filter((r) => r.take)
  const valid = name.trim() && host.trim() && username.trim() && (nvr || password) && taken.every((r) => r.name.trim())

  return (
    <Card>
      <CardHeader><CardTitle>{nvr ? nvr.name : t('ipcam.add')}</CardTitle></CardHeader>
      <CardContent>
        <form onSubmit={save} className="space-y-3 text-sm">
          <Input placeholder={t('ipcam.name')} value={name} onChange={(e) => setName(e.target.value)} />
          <div className="flex gap-2">
            <Input className="flex-1" placeholder={t('ipcam.host')} value={host} onChange={(e) => setHost(e.target.value)} />
            <Input className="w-24" type="number" min={1} max={65535} value={port} onChange={(e) => setPort(Number(e.target.value))} aria-label={t('ipcam.port')} />
            <Input className="w-24" type="number" min={1} max={65535} value={rtspPort} onChange={(e) => setRtspPort(Number(e.target.value))} aria-label={t('ipcam.rtspPort')} title={t('ipcam.rtspPort')} />
          </div>
          <Input placeholder={t('ipcam.username')} value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="off" />
          <PasswordInput placeholder={nvr ? t('ipcam.passwordKeep') : t('ipcam.password')} value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="new-password" />
          <p className="text-xs text-neutral-500">{t('ipcam.loginHint')}</p>
          <Button variant="secondary" onClick={() => void check()} disabled={busy || !host.trim() || !username.trim() || (!nvr && !password)}>
            <Search />{t('ipcam.probe')}
          </Button>
          {probe && <ProbeLine probe={probe} />}

          {rows.length > 0 && (
            <div className="space-y-2">
              <p className="text-xs text-neutral-400">{t('ipcam.pickChannels')}</p>
              {rows.map((r) => (
                <div key={r.channel} className={`space-y-2 rounded-lg border p-2 ${r.take ? 'border-amber-400/60' : 'border-neutral-800'}`}>
                  <label className="flex cursor-pointer items-center gap-2">
                    <input type="checkbox" className="size-4 accent-amber-400" checked={r.take} onChange={(e) => set(r.channel, { take: e.target.checked })} />
                    <span>{t('ipcam.channel')} {r.channel}{r.title ? ` · ${r.title}` : ''}</span>
                    {r.connected === false && <Badge tone="warn">{t('ipcam.noCamera')}</Badge>}
                  </label>
                  {r.take && (
                    <>
                      <div className="flex flex-wrap gap-2">
                        <Input className="min-w-40 flex-1" placeholder={t('add.namePlaceholder')} value={r.name} onChange={(e) => set(r.channel, { name: e.target.value })} />
                        <Select className="h-10" value={r.zoneId ?? ''} onChange={(e) => set(r.channel, { zoneId: e.target.value ? Number(e.target.value) : null })} aria-label={t('ipcam.zone')}>
                          <option value="">{t('ipcam.noZone')}</option>
                          {(zones.data ?? []).map((z) => <option key={z.id} value={z.id}>{z.name}</option>)}
                        </Select>
                      </div>
                      <label className="flex cursor-pointer items-center gap-2 text-xs text-neutral-400">
                        <input type="checkbox" className="size-4 accent-amber-400" checked={r.recordWhenDisarmed} onChange={(e) => set(r.channel, { recordWhenDisarmed: e.target.checked })} />
                        {t('ipcam.recordWhenDisarmed')}
                      </label>
                      <label className="flex flex-wrap items-center gap-2 text-xs text-neutral-400">
                        {t('ipcam.snapshotSource')}
                        <Select className="h-9" value={r.snapshotSource} onChange={(e) => set(r.channel, { snapshotSource: e.target.value })}>
                          {snapshotSources.map((s) => <option key={s} value={s}>{t(`ipcam.sources.${s}`)}</option>)}
                        </Select>
                      </label>
                      <label className="flex flex-wrap items-center gap-2 text-xs text-neutral-400">
                        {t('ipcam.snapshotQuality')}
                        <Select className="h-9" value={r.snapshotQuality} onChange={(e) => set(r.channel, { snapshotQuality: e.target.value })}>
                          {snapshotQualities.map((q) => <option key={q} value={q}>{t(`ipcam.qualities.${q}`)}</option>)}
                        </Select>
                      </label>
                    </>
                  )}
                </div>
              ))}
            </div>
          )}
          <div className="flex gap-2">
            <Input className="w-32" type="number" min={1} max={256} placeholder={t('ipcam.channelNumber')} value={extra} onChange={(e) => setExtra(e.target.value)} />
            <Button variant="ghost" onClick={addChannel} disabled={!extra}><Plus />{t('ipcam.addChannel')}</Button>
          </div>

          {error && <p className="text-sm text-red-400">{error}</p>}
          <div className="flex flex-wrap gap-2">
            <Button type="submit" disabled={busy || !valid}>{t('common.save')}</Button>
            <Button variant="ghost" onClick={onCancel}>{t('common.cancel')}</Button>
          </div>
          <p className="text-xs text-neutral-500">{t('ipcam.guardHint')}</p>
        </form>
      </CardContent>
    </Card>
  )
}
