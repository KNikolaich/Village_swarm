import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowRightLeft, CloudDownload, Cpu, Moon, Plus, Terminal } from 'lucide-react'
import { useMemo, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { OtaRolloutPanel } from '@/components/OtaRolloutPanel'
import { toast } from 'sonner'
import { api, call, type DeviceDto } from '@/api/client'
import { keys, useDevices, useLatestTelemetry, useMe, useOtaRollout, useZones } from '@/api/queries'
import { Button } from '@/components/ui/button'
import { Card, CardContent, Empty, Select, Skeleton } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { hiveName } from '@/lib/hiveStats'
import { formatAgo } from '@/lib/format'
import { candidates, newerBuild as newerBuildFor } from '@/lib/otaRollout'
import { groupDevices, groupings, type Grouping, splitChildren } from '@/lib/swarm'
import { cn } from '@/lib/utils'
import { FirmwarePanel } from '@/components/FirmwarePanel'
import { Readings, StatusIcon, SleepHelp, QuickControls } from '@/components/DeviceReadings'
import { ChildDevices } from '@/components/ChildDevices'
import { asleep } from '@/lib/deviceSleep'

/** Battery intervals offered in the UI; the contract allows 60..86400 s. 0 = always awake. */
const sleepChoices = [0, 300, 600, 900, 1800, 3600]

/** Screen 5 (spec 8.2): every hornet, grouped by zone or kind (hives first), with its readings and controls at hand. */
export function SwarmPage() {
  const qc = useQueryClient()
  const devices = useDevices(true)
  const me = useMe()
  // On the city node the hornets made here are marked: the others are copies of home's.
  const cluster = useQuery({ queryKey: ['cluster'], queryFn: () => call(api.GET('/api/cluster')), refetchInterval: 60_000, retry: false })
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
  // An NVR's cameras are listed under it, not in the groups.
  const { top, children } = useMemo(() => splitChildren(list), [list])
  const [folded, setFolded] = useState<ReadonlySet<string>>(() => new Set())
  const toggleFolded = (id: string) => setFolded((f) => {
    const next = new Set(f)
    if (next.has(id)) next.delete(id)
    else next.add(id)
    return next
  })
  const groups = useMemo(
    () => groupDevices(top, zones.data ?? [], grouping, {
      hives: t('swarm.groupHives'),
      noZone: t('swarm.noZone'),
      type: (type) => t(`deviceTypes.${type}`, type),
    }),
    [top, zones.data, grouping],
  )

  /** The wizard opens on the flashing step: the hornet's type and name are known, and the code is issued there. */
  function replace(deviceId: string, revive = false) {
    if ((devices.data ?? []).find((d) => d.deviceId === deviceId)?.driver === 'tasmota') {
      if (window.confirm(t('swarm.replaceTasmotaConfirm').replace('{id}', deviceId))) navigate(`/swarm/add?tasmota=${encodeURIComponent(deviceId)}`)
      return
    }
    if ((devices.data ?? []).find((d) => d.deviceId === deviceId)?.driver === 'http') {
      // a remote monitor has no enrollment: the same setup as for a new one, for the monitor that exists
      navigate(`/swarm/add?remote=${encodeURIComponent(deviceId)}`)
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
              <div className="flex min-w-0 flex-1 basis-48 items-start gap-2.5">
                <div className="pt-0.5"><StatusIcon device={d} /></div>
                <div className="min-w-0 flex-1">
                  <Link to={`/swarm/${encodeURIComponent(d.deviceId)}`} className="block truncate font-medium hover:text-amber-400">{hiveName(d, cluster.data)}</Link>
                  <p className="truncate text-xs text-neutral-500">
                    {d.deviceId} · {d.type === 'nvr' ? t('deviceTypes.nvr') : d.type}{d.driver === 'tasmota' ? ' · Tasmota' : d.driver === 'zigbee' ? ' · Zigbee' : ''}{d.type === 'nvr' && d.ip ? ` · ${d.ip}` : ''} · {formatAgo(d.lastSeenAt)}{d.local && cluster.data?.enabled && cluster.data.node === 'city' ? ` · ${t('swarm.localHere')}` : ''}
                  </p>
                  {d.type === 'nvr' && loginTrouble(d) && <p className="text-xs text-red-400">{t(`ipcam.login.short.${loginTrouble(d)}`)}</p>}
                  {canControl && d.type === 'meteo' && d.status !== 'disabled' && (
                    <div className="mt-1 flex flex-wrap items-center gap-2 text-xs text-neutral-400">
                      <span className="flex items-center gap-2 whitespace-nowrap"><Moon className="size-3" />{t('swarm.sleep')}</span>
                      <Select
                        value={d.sleepS ?? 0}
                        onChange={(e) => void setSleep(d, { sleepS: Number(e.target.value) })}
                        className="h-7 w-auto text-xs"
                        aria-label={t('swarm.sleep')}
                      >
                        {sleepChoices.map((s) => (
                          <option key={s} value={s}>{s === 0 ? t('swarm.sleepNever') : t('swarm.sleepMinutes').replace('{n}', String(s / 60))}</option>
                        ))}
                      </Select>
                      {!!d.sleepS && (
                        <Select
                          value={d.sleepMode}
                          onChange={(e) => void setSleep(d, { sleepMode: e.target.value as 'deep' | 'light' })}
                          className="h-7 w-auto text-xs"
                          aria-label={t('swarm.sleepMode')}
                        >
                          <option value="deep">{t('swarm.sleepDeep')}</option>
                          <option value="light">{t('swarm.sleepLight')}</option>
                        </Select>
                      )}
                      <SleepHelp />
                    </div>
                  )}
                </div>
              </div>
              {d.status !== 'disabled' && <Readings device={d} latest={latest.data ?? []} />}
              <div className="ml-auto flex shrink-0 items-center gap-2">
                {canControl && d.status === 'online' && !asleep(d) && <QuickControls device={d} />}
                {d.rssi != null && d.status === 'online' && d.rssi < -80 && (
                  <span className="text-xs tabular-nums text-amber-400" title={t('swarm.weakSignal')}>{d.rssi} dBm</span>
                )}
                {/* The chip only when there is a newer build for it; the version it runs is on its page. */}
                {isAdmin && d.status !== 'disabled' && d.driver === 'hornet' && d.type !== 'hive' && newerBuild(d) && (
                  <Button variant="ghost" size="icon" title={t('swarm.updateAvailable')} aria-label={t('swarm.updateAvailable')}
                    className="text-amber-400 hover:text-amber-300" onClick={() => setFwOpen(fwOpen === d.deviceId ? null : d.deviceId)}><Cpu /></Button>
                )}
                {isAdmin && d.status === 'disabled' && replaceable(d) && (
                  <Button variant="outline" size="sm" title={t('swarm.reviveTitle')} onClick={() => replace(d.deviceId, true)}><ArrowRightLeft />{t('swarm.revive')}</Button>
                )}
                {isAdmin && d.status !== 'disabled' && d.type !== 'hive' && replaceable(d) && (
                  <Button variant="ghost" size="icon" title={t('swarm.replaceTitle')} onClick={() => replace(d.deviceId)}><ArrowRightLeft /></Button>
                )}
              </div>
            </CardContent>
            {fwOpen === d.deviceId && (
              <CardContent className="border-t border-neutral-800 pt-3">
                <FirmwarePanel device={d} builds={firmware.data ?? []} onDone={() => setFwOpen(null)} />
              </CardContent>
            )}
            {children.has(d.deviceId) && (
              <ChildDevices
                devices={children.get(d.deviceId)!}
                open={!folded.has(d.deviceId)}
                onToggle={() => toggleFolded(d.deviceId)}
                canControl={canControl}
              />
            )}
          </Card>
            ))}
          </section>
        ))}
      </div>
    </div>
  )
}

/** Only boards can be replaced (a new board takes over the hornet): not Zigbee devices or cameras behind an NVR. */
const replaceable = (d: DeviceDto) => d.driver === 'hornet' || d.driver === 'tasmota' || d.driver === 'http'

/** An NVR whose login is locked or refused says so on its row (its device state "login", contracts/ipcam.md). */
function loginTrouble(d: DeviceDto): string | null {
  const login = (d.state as { login?: unknown } | null)?.login
  return typeof login === 'string' && login !== 'ok' ? login : null
}
