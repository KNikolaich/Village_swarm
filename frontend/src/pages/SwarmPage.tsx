import { useQueryClient } from '@tanstack/react-query'
import { Moon, Plus, RefreshCw, Trash2, Wifi, WifiOff } from 'lucide-react'
import { useMemo, useState } from 'react'
import { Link } from 'react-router'
import { toast } from 'sonner'
import { api, call, type DeviceDto } from '@/api/client'
import { keys, useDevices, useMe } from '@/api/queries'
import { Button } from '@/components/ui/button'
import { Badge, Card, CardContent, Empty, Select, Skeleton } from '@/components/ui/primitives'
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
  if (!d.sleepS || d.status !== 'online' || !d.lastSeenAt) return false
  return Date.now() - new Date(d.lastSeenAt).getTime() > 90_000
}

/** Screen 5 (spec 8.2): every hornet, sorted by trouble, with replace and remove. */
export function SwarmPage() {
  const qc = useQueryClient()
  const devices = useDevices()
  const me = useMe()
  const isAdmin = me.data?.roles.includes('admin') ?? false
  const canControl = isAdmin || (me.data?.roles.includes('member') ?? false)
  const [replaceCode, setReplaceCode] = useState<{ deviceId: string; code: string; expiresAt: string } | null>(null)
  const list = useMemo(
    () => [...(devices.data ?? [])].sort((a, b) => badness(a) - badness(b) || a.deviceId.localeCompare(b.deviceId)),
    [devices.data],
  )

  async function replace(deviceId: string) {
    const code = await call(api.POST('/api/devices/{deviceId}/replace', { params: { path: { deviceId } } }))
    setReplaceCode({ deviceId, code: code.code, expiresAt: code.expiresAt })
  }

  async function setSleep(d: DeviceDto, sleepS: number) {
    await call(api.PATCH('/api/devices/{deviceId}/config', { params: { path: { deviceId: d.deviceId } }, body: { sleepS } }))
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
                      onChange={(e) => void setSleep(d, Number(e.target.value))}
                      className="h-7 text-xs"
                    >
                      {sleepChoices.map((s) => (
                        <option key={s} value={s}>{s === 0 ? t('swarm.sleepNever') : t('swarm.sleepMinutes').replace('{n}', String(s / 60))}</option>
                      ))}
                    </Select>
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
                  <>
                    <Button variant="ghost" size="icon" title={t('swarm.replace')} onClick={() => void replace(d.deviceId)}><RefreshCw /></Button>
                    <Button variant="ghost" size="icon" title={t('swarm.remove')} onClick={() => void remove(d)}><Trash2 /></Button>
                  </>
                )}
              </div>
            </CardContent>
          </Card>
        ))}
      </div>
    </div>
  )
}
