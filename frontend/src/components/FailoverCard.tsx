import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState, type FormEvent } from 'react'
import { toast } from 'sonner'
import { api, call, failureText } from '@/api/client'
import { useMe } from '@/api/queries'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle, Input } from '@/components/ui/primitives'
import { HelpTip } from '@/components/HelpTip'
import { t } from '@/i18n'
import { formatAgo } from '@/lib/format'

/**
 * The second broker of the hornets (spec 4.5): where they find the city broker when this hive does not answer. The address is the
 * one the HORNETS reach (for example through the router's WireGuard tunnel), not the one this hive uses. Empty: no second broker.
 */
export function FailoverCard() {
  const me = useMe()
  const qc = useQueryClient()
  const isAdmin = me.data?.roles.includes('admin') ?? false
  const state = useQuery({ queryKey: ['failover'], queryFn: () => call(api.GET('/api/failover')), enabled: isAdmin })
  const replica = useQuery({ queryKey: ['replica', 'media'], queryFn: () => call(api.GET('/api/replica/media')), enabled: isAdmin, refetchInterval: 30_000 })
  const [host, setHost] = useState('')
  const [replicaUrl, setReplicaUrl] = useState('')
  const [certSha256, setCertSha256] = useState('')
  const [port, setPort] = useState('1883')
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    if (!state.data) return
    setHost(state.data.host ?? '')
    setReplicaUrl(state.data.replicaUrl ?? '')
    setCertSha256(state.data.certSha256 ?? '')
    setPort(String(state.data.port))
  }, [state.data])

  if (!isAdmin) return null

  const portNumber = Number(port)
  const valid = (host.trim() === '' || (Number.isInteger(portNumber) && portNumber >= 1 && portNumber <= 65535))
    && (replicaUrl.trim() === '' || /^https?:\/\/\S+$/i.test(replicaUrl.trim()))
    && (certSha256.trim() === '' || /^[0-9a-f]{64}$/i.test(certSha256.replace(/[:\s-]/g, '')))

  async function save(e: FormEvent) {
    e.preventDefault()
    setBusy(true)
    try {
      const { response, error } = await api.PUT('/api/failover', { body: { host: host.trim() || null, port: portNumber || 1883, replicaUrl: replicaUrl.trim() || null, certSha256: certSha256.trim() || null } })
      if (!response.ok) {
        toast.error(failureText({ error }, t('failover.bad')))
        return
      }
      toast.success(t('failover.saved'))
      await qc.invalidateQueries({ queryKey: ['failover'] })
      await qc.invalidateQueries({ queryKey: ['replica'] })
    } finally {
      setBusy(false)
    }
  }

  async function forget() {
    const { response, error } = await api.DELETE('/api/replica/pin')
    if (!response.ok) {
      toast.error(failureText({ error }, t('failover.bad')))
      return
    }
    toast.success(t('failover.pinForgotten'))
    await qc.invalidateQueries({ queryKey: ['replica'] })
  }

  const pin = replica.data?.pin
  return (
    <Card>
      <CardHeader><CardTitle className="flex items-center gap-1">{t('failover.title')}<HelpTip text={t('failover.hint')} /></CardTitle></CardHeader>
      <CardContent className="space-y-3 text-sm">
        {state.data && !state.data.canLogin && <p className="text-xs text-amber-300">{t('failover.noSecret')}</p>}
        <form className="grid grid-cols-[1fr_6rem] items-end gap-2 sm:max-w-md" onSubmit={(e) => void save(e)}>
          <label className="space-y-1">
            <span className="text-xs text-neutral-400">{t('failover.host')}</span>
            <Input value={host} placeholder="192.168.1.3" onChange={(e) => setHost(e.target.value)} />
          </label>
          <label className="space-y-1">
            <span className="text-xs text-neutral-400">{t('failover.port')}</span>
            <Input inputMode="numeric" value={port} onChange={(e) => setPort(e.target.value)} />
          </label>
          <label className="col-span-2 space-y-1">
            <span className="text-xs text-neutral-400">{t('failover.replicaUrl')}</span>
            <Input value={replicaUrl} placeholder={host.trim() ? `https://${host.trim()}` : 'https://192.168.1.3'} onChange={(e) => setReplicaUrl(e.target.value)} />
          </label>
          <label className="col-span-2 space-y-1">
            <span className="text-xs text-neutral-400">{t('failover.certSha256')}</span>
            <Input value={certSha256} placeholder="AA:BB:CC:…" className="font-mono" onChange={(e) => setCertSha256(e.target.value)} />
          </label>
          <div className="col-span-2 flex items-center gap-3">
            <Button type="submit" disabled={busy || !valid}>{t('common.save')}</Button>
            {host.trim() === '' && <span className="text-xs text-neutral-500">{t('failover.off')}</span>}
          </div>
        </form>
        {replica.data && (
          <div className="space-y-1 border-t border-neutral-800 pt-3 text-xs">
            <p className="text-neutral-300">{t('failover.photos')}</p>
            {!replica.data.enabled && <p className="text-neutral-500">{t('failover.photosOff')}</p>}
            {replica.data.enabled && (
              <>
                <p className="text-neutral-400">
                  {replica.data.waiting === 0
                    ? t('failover.photosNone')
                    : t('failover.photosWaiting').replace('{n}', String(replica.data.waiting)).replace('{alarms}', String(replica.data.waitingAlarms))}
                  {replica.data.lastSentAt ? ` ${t('failover.photosLast').replace('{when}', formatAgo(replica.data.lastSentAt))}` : ''}
                </p>
                {replica.data.lastError && <p className="text-amber-300">{replica.data.lastError}</p>}
                {pin && (
                  <div className="space-y-1">
                    <p className="text-neutral-400">
                      {pin.pinned ? t(`failover.pin.${pin.source}`) : t('failover.pin.none')}
                    </p>
                    {pin.pinned && <p className="break-all font-mono text-neutral-500">{pin.pinned}</p>}
                    {pin.seen && <p className="break-all text-amber-300">{t('failover.pinMismatch')} <span className="font-mono">{pin.seen}</span></p>}
                    {pin.source === 'learned' && (
                      <Button type="button" variant="outline" onClick={() => void forget()}>{t('failover.pinForget')}</Button>
                    )}
                  </div>
                )}
              </>
            )}
          </div>
        )}
      </CardContent>
    </Card>
  )
}
