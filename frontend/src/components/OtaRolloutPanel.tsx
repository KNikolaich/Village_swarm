import { useQueryClient } from '@tanstack/react-query'
import { useEffect, useMemo, useRef, useState } from 'react'
import { Link } from 'react-router'
import { toast } from 'sonner'
import { api, call, type DeviceDto, type Schemas } from '@/api/client'
import { keys } from '@/api/queries'
import { Button } from '@/components/ui/button'
import { Badge, type BadgeTone } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { formatAgo } from '@/lib/format'
import { candidates, preselected, toSend } from '@/lib/otaRollout'
import { cn } from '@/lib/utils'

type Rollout = Schemas['OtaRolloutDto']
type ItemState = Schemas['OtaRolloutItemState']

const itemTone: Record<ItemState, BadgeTone> = { pending: 'neutral', updating: 'warn', done: 'ok', failed: 'alarm', skipped: 'neutral' }
const active = (r: Rollout | null | undefined) => r?.state === 'running' || r?.state === 'stopping'

/** Progress of a rollout: each hornet with its state, the current one marked, and Stop while it runs. */
function Progress({ rollout, onNew }: { rollout: Rollout; onNew: () => void }) {
  const qc = useQueryClient()
  const [busy, setBusy] = useState(false)
  const done = rollout.items.filter((i) => i.state === 'done').length

  async function stop() {
    if (!window.confirm(t('swarm.rollout.stopConfirm'))) return
    setBusy(true)
    try {
      qc.setQueryData(keys.otaRollout, await call(api.POST('/api/firmware/rollout/stop')))
    } catch {
      toast.error(t('errors.generic'))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-sm">
          <span className={cn('font-medium', rollout.state === 'failed' && 'text-red-400')}>{t(`swarm.rollout.state.${rollout.state}`)}</span>
          <span className="text-neutral-400"> · {t('events.otaRollout.count').replace('{done}', String(done)).replace('{total}', String(rollout.items.length))}</span>
        </p>
        {rollout.state === 'running' && <Button size="sm" variant="outline" disabled={busy} onClick={() => void stop()}>{t('swarm.rollout.stop')}</Button>}
        {!active(rollout) && <Button size="sm" variant="outline" onClick={onNew}>{t('swarm.rollout.again')}</Button>}
      </div>
      <ul className="space-y-1 text-sm">
        {rollout.items.map((i) => (
          <li key={i.deviceId} className="flex flex-wrap items-center justify-between gap-x-3 gap-y-0.5">
            <span className="min-w-0 truncate">
              {i.name}
              <span className="text-xs text-neutral-500"> · {i.fromVersion ?? '?'} → {i.toVersion}</span>
              {i.state === 'updating' && i.startedAt && <span className="text-xs text-neutral-500"> · {formatAgo(i.startedAt)}</span>}
            </span>
            <span className="flex items-center gap-2">
              {i.error && <span className="text-xs text-neutral-400">{t(`swarm.rollout.why.${i.error}`, i.error)}</span>}
              <Badge tone={itemTone[i.state]}>{t(`swarm.rollout.item.${i.state}`)}</Badge>
            </span>
          </li>
        ))}
      </ul>
      {rollout.state === 'failed' && (
        <p className="text-xs text-neutral-400">
          {t('swarm.rollout.failedHint')} <Link to="/events" className="text-amber-400">{t('settings.wifiPanel.openEvents')}</Link>
        </p>
      )}
    </div>
  )
}

/**
 * "Update all outdated" on the Swarm screen: the hornets with a newer build on the hive, those that can take it now
 * ticked; ESP8266 boards (no rollback) only with a separate confirmation. The hive then updates them one at a time.
 */
export function OtaRolloutPanel({ devices, builds, rollout, onClose }: {
  devices: readonly DeviceDto[]
  builds: readonly Schemas['FirmwareBuildDto'][]
  rollout: Rollout | null
  onClose: () => void
}) {
  const qc = useQueryClient()
  const list = useMemo(() => candidates(devices, builds), [devices, builds])
  const [chosen, setChosen] = useState<string[]>(() => preselected(list))
  const [confirmNoRollback, setConfirmNoRollback] = useState(false)
  const [busy, setBusy] = useState(false)
  // A finished rollout stays on screen until a new one is asked for.
  const [fresh, setFresh] = useState(false)
  const showProgress = !!rollout && (active(rollout) || !fresh)
  const sending = toSend(list, chosen, confirmNoRollback)

  // Once a rollout ends, the swarm list and the chips catch up with the new versions.
  const wasActive = useRef(active(rollout))
  useEffect(() => {
    if (wasActive.current && !active(rollout)) void qc.invalidateQueries({ queryKey: keys.devices })
    wasActive.current = active(rollout)
  }, [rollout, qc])

  async function start() {
    const names = list.filter((c) => sending.includes(c.device.deviceId)).map((c) => c.device.name)
    if (sending.length === 0 || !window.confirm(t('swarm.rollout.confirm').replace('{names}', names.join(', ')))) return
    setBusy(true)
    try {
      const { data, response } = await api.POST('/api/firmware/rollout', { body: { deviceIds: sending, confirmNoRollback } })
      if (data) {
        qc.setQueryData(keys.otaRollout, data)
        setFresh(false)
      } else toast.error(t(response.status === 409 ? 'swarm.rollout.busy' : response.status === 400 ? 'swarm.rollout.nothing' : 'errors.generic'))
    } catch {
      toast.error(t('errors.generic'))
    } finally {
      setBusy(false)
    }
  }

  function startOver() {
    setFresh(true)
    setChosen(preselected(list))
    setConfirmNoRollback(false)
  }

  const hasEsp8266 = list.some((c) => c.noRollback && !c.blocker)

  return (
    <div className="space-y-3 rounded-xl border border-neutral-800 bg-neutral-900/70 p-3">
      <div className="flex items-start justify-between gap-3">
        <p className="text-sm font-medium">{t('swarm.rollout.title')}</p>
        {!active(rollout) && <Button size="sm" variant="ghost" onClick={onClose}>{t('swarm.rollout.close')}</Button>}
      </div>

      {showProgress && rollout ? (
        <Progress rollout={rollout} onNew={startOver} />
      ) : (
        <>
          <p className="text-xs text-neutral-500">{t('swarm.rollout.intro')}</p>
          <ul className="space-y-1">
            {list.map((c) => {
              const why = c.blocker ? t(`swarm.rollout.${c.blocker}`) : ''
              const locked = !!why || (c.noRollback && !confirmNoRollback)
              return (
                <li key={c.device.deviceId}>
                  <label className={cn('flex flex-wrap items-center gap-x-2 gap-y-0.5 text-sm', locked ? 'cursor-not-allowed opacity-60' : 'cursor-pointer')}>
                    <input
                      type="checkbox"
                      className="size-4 accent-amber-400"
                      disabled={locked}
                      checked={chosen.includes(c.device.deviceId) && !locked}
                      onChange={(e) => setChosen(e.target.checked ? [...chosen, c.device.deviceId] : chosen.filter((x) => x !== c.device.deviceId))}
                    />
                    <span>{c.device.name}</span>
                    <span className="text-xs text-neutral-500">{c.device.fw ?? '?'} → {c.build.version}</span>
                    {c.noRollback && <span className="text-xs text-amber-400">{t('swarm.rollout.esp8266')}</span>}
                    {why && <span className="text-xs text-amber-400">{why}</span>}
                  </label>
                </li>
              )
            })}
          </ul>

          {hasEsp8266 && (
            <label className="flex cursor-pointer items-start gap-2 rounded-lg border border-amber-900/60 bg-amber-950/20 p-2 text-xs text-amber-300">
              <input
                type="checkbox"
                className="mt-0.5 size-4 shrink-0 accent-amber-400"
                checked={confirmNoRollback}
                onChange={(e) => {
                  setConfirmNoRollback(e.target.checked)
                  // Ticking the confirmation ticks the ESP8266 boards that can take it now; unticking leaves them out.
                  const esp = list.filter((c) => c.noRollback && !c.blocker).map((c) => c.device.deviceId)
                  setChosen(e.target.checked ? [...new Set([...chosen, ...esp])] : chosen.filter((id) => !esp.includes(id)))
                }}
              />
              {t('swarm.rollout.confirmNoRollback')}
            </label>
          )}

          <div className="rounded-lg bg-neutral-800/50 p-3 text-xs text-neutral-300">
            {sending.length === 0 ? t('swarm.rollout.summaryNone') : t('swarm.rollout.summary').replace('{n}', String(sending.length))}
          </div>
          <Button disabled={busy || sending.length === 0} onClick={() => void start()}>{t('swarm.rollout.start')}</Button>
        </>
      )}
    </div>
  )
}
