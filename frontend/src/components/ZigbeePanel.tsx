import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState, type FormEvent } from 'react'
import { toast } from 'sonner'
import { api, call } from '@/api/client'
import { Button } from '@/components/ui/button'
import { Badge, Card, CardContent, CardHeader, CardTitle, Input, Select } from '@/components/ui/primitives'
import { t } from '@/i18n'

const idPattern = /^[a-z][a-z0-9]*(-[a-z0-9]+)*$/

// What a Zigbee device can be in the hive.
const zigbeeTypes = ['sensor', 'relay', 'meteo', 'leak']

/** Seconds left of pairing mode, counting down on the screen. */
function useCountdown(until: string | null | undefined) {
  const [now, setNow] = useState(Date.now())
  useEffect(() => {
    if (!until) return
    const timer = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(timer)
  }, [until])
  return until ? Math.max(0, Math.round((new Date(until).getTime() - now) / 1000)) : 0
}

/**
 * Zigbee through zigbee2mqtt (docs/zigbee.md): allow devices to join, see what was found, give it an id and a name.
 * The hive renames the device in zigbee2mqtt to that id, which is how its reports find their way back.
 */
export function ZigbeePanel() {
  const qc = useQueryClient()
  const key = ['zigbee']
  const zigbee = useQuery({ queryKey: key, queryFn: () => call(api.GET('/api/zigbee')), refetchInterval: 3000 })
  const data = zigbee.data
  const left = useCountdown(data?.permitJoinUntil)
  const [adding, setAdding] = useState<string | null>(null)
  const [type, setType] = useState('sensor')
  const [deviceId, setDeviceId] = useState('')
  const [name, setName] = useState('')
  const [error, setError] = useState<string | null>(null)

  async function permit(seconds: number) {
    const { response } = await api.POST('/api/zigbee/permit-join', { body: { seconds } })
    if (!response.ok) toast.error(t('zigbee.permitFailed'))
    await qc.invalidateQueries({ queryKey: key })
  }

  async function add(e: FormEvent) {
    e.preventDefault()
    setError(null)
    const { response, error: err } = await api.POST('/api/zigbee/devices', { body: { ieee: adding!, deviceId, type, name } })
    if (!response.ok) {
      setError((err as { errors?: Record<string, string[]> } | undefined)?.errors?.request?.[0] ?? t('errors.generic'))
      return
    }
    toast.success(t('zigbee.added'))
    setAdding(null)
    setDeviceId('')
    setName('')
    await qc.invalidateQueries({ queryKey: key })
  }

  if (!data) return null
  if (!data.enabled)
    return (
      <Card>
        <CardContent><p className="py-4 text-sm text-neutral-400">{t('zigbee.off')}</p></CardContent>
      </Card>
    )

  const found = data.devices.filter((d) => !d.deviceId)
  const known = data.devices.filter((d) => d.deviceId)
  const idValid = idPattern.test(deviceId) && deviceId.length <= 32

  return (
    <div className="space-y-4">
      <Card>
        <CardHeader>
          <CardTitle>{t('zigbee.title')}</CardTitle>
          <Badge tone={data.bridge === 'online' ? 'ok' : data.bridge === 'offline' ? 'alarm' : 'neutral'}>{t(`zigbee.bridge.${data.bridge}`, data.bridge)}</Badge>
        </CardHeader>
        <CardContent className="space-y-3 text-sm">
          <p className="text-neutral-400">{t('zigbee.hint')}</p>
          {data.bridge !== 'online' && <p className="text-amber-300">{t('zigbee.bridgeDown')}</p>}
          {left > 0 ? (
            <div className="flex items-center gap-3">
              <span className="text-emerald-400">{t('zigbee.pairing').replace('{s}', String(left))}</span>
              <Button variant="secondary" onClick={() => void permit(0)}>{t('zigbee.stop')}</Button>
            </div>
          ) : (
            <Button disabled={data.bridge !== 'online'} onClick={() => void permit(120)}>{t('zigbee.start')}</Button>
          )}
        </CardContent>
      </Card>

      <Card>
        <CardHeader><CardTitle>{t('zigbee.found')}</CardTitle></CardHeader>
        <CardContent className="space-y-3 text-sm">
          {found.length === 0 && <p className="text-neutral-500">{t('zigbee.none')}</p>}
          {found.map((d) => (
            <div key={d.ieee} className="space-y-2 rounded-lg border border-neutral-800 p-3">
              <div className="flex items-center justify-between gap-2">
                <div className="min-w-0">
                  <p className="truncate font-medium">{[d.vendor, d.model].filter(Boolean).join(' ') || d.ieee}</p>
                  <p className="truncate text-xs text-neutral-500">{d.description ?? ''} {d.ieee}</p>
                  {!d.interviewCompleted && <p className="text-xs text-amber-300">{t('zigbee.interviewing')}</p>}
                  {d.interviewCompleted && !d.supported && <p className="text-xs text-amber-300">{t('zigbee.unsupported')}</p>}
                </div>
                <Button size="sm" disabled={!d.interviewCompleted} onClick={() => setAdding(adding === d.ieee ? null : d.ieee)}>{t('zigbee.add')}</Button>
              </div>
              {adding === d.ieee && (
                <form onSubmit={add} className="space-y-2">
                  <Select value={type} onChange={(e) => setType(e.target.value)} className="h-11 w-full">
                    {zigbeeTypes.map((c) => <option key={c} value={c}>{t(`deviceTypes.${c}`)}</option>)}
                  </Select>
                  <Input placeholder={t('add.idPlaceholder')} value={deviceId} onChange={(e) => setDeviceId(e.target.value.toLowerCase())} />
                  {deviceId && !idValid && <p className="text-xs text-amber-300">{t('add.idHint')}</p>}
                  <Input placeholder={t('add.namePlaceholder')} value={name} onChange={(e) => setName(e.target.value)} />
                  {error && <p className="text-sm text-red-400">{error}</p>}
                  <Button type="submit" disabled={!idValid || !name.trim()}>{t('zigbee.addGo')}</Button>
                </form>
              )}
            </div>
          ))}
          {known.length > 0 && (
            <div className="space-y-1 border-t border-neutral-800 pt-3">
              <p className="text-xs text-neutral-500">{t('zigbee.known')}</p>
              {known.map((d) => <p key={d.ieee} className="text-xs text-neutral-400">{d.deviceId} · {[d.vendor, d.model].filter(Boolean).join(' ')}</p>)}
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  )
}
