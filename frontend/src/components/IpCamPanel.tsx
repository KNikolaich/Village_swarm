import { LiveSnapshotButton, ChannelDiagnostics } from '@/components/NvrLines'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { toast } from 'sonner'
import { api, call } from '@/api/client'
import { keys } from '@/api/queries'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle, Select } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { type Nvr, snapshotSources, snapshotQualities, nvrsKey, problem } from '@/lib/ipcam'
import { CameraForm, CameraConnection } from '@/components/CameraForm'
import { NvrCard } from '@/components/NvrCard'
import { NvrForm } from '@/components/NvrForm'

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
