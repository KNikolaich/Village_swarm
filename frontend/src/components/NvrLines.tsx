import { Camera, CheckCircle2 } from 'lucide-react'
import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { t } from '@/i18n'
import { type Nvr, type Probe, clock, errorText } from '@/lib/ipcam'

/**
 * "Снимок" next to a channel: a frame taken right now the way photos are taken (snapshot.cgi, then the RTSP stream),
 * or the NVR's own reason when there is none, so a camera that gives no photos can be told apart from one that gives no motion.
 */
export function LiveSnapshotButton({ nvrId, channel }: { nvrId: number; channel: number }) {
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
export function LoginLine({ login }: { login: Nvr['login'] }) {
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
export function StreamLine({ stream }: { stream: Nvr['stream'] }) {
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
export function ChannelDiagnostics({ channel }: { channel: Nvr['channels'][number] }) {
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

export function ProbeLine({ probe }: { probe: Probe }) {
  return probe.ok ? (
    <p className="flex items-center gap-1 text-emerald-400"><CheckCircle2 className="size-4" />{t('ipcam.probeOk').replace('{type}', probe.deviceType ?? '').replace('{n}', String(probe.channels.length))}</p>
  ) : (
    <p className="text-red-400" title={probe.detail ?? undefined}>{errorText(probe.error)}</p>
  )
}
