import { ProbeLine } from '@/components/NvrLines'
import { Plus, Search } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { toast } from 'sonner'
import { api, call } from '@/api/client'
import { useZones } from '@/api/queries'
import { PasswordInput } from '@/components/PasswordInput'
import { Button } from '@/components/ui/button'
import { Badge, Card, CardContent, CardHeader, CardTitle, Input, Select } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { type Nvr, type Probe, type Row, snapshotSources, snapshotQualities, problem } from '@/lib/ipcam'

export function NvrForm({ nvr, onDone, onCancel }: { nvr?: Nvr; onDone: (saved: Nvr) => void; onCancel: () => void }) {
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
