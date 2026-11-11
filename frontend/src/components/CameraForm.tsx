import { LoginLine, StreamLine } from '@/components/NvrLines'
import { useQueryClient } from '@tanstack/react-query'
import { CheckCircle2, Pencil, Search, Trash2 } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router'
import { toast } from 'sonner'
import { api, call } from '@/api/client'
import { keys, useZones } from '@/api/queries'
import { PasswordInput } from '@/components/PasswordInput'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle, Input, Select } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { type Nvr, type Probe, nvrsKey, errorText, problem } from '@/lib/ipcam'

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
export function CameraForm({ nvr, onDone, onCancel }: { nvr?: Nvr; onDone: (saved: Nvr) => void; onCancel: () => void }) {
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
export function CameraConnection({ nvr }: { nvr: Nvr }) {
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
