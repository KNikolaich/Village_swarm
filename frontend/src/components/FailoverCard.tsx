import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState, type FormEvent } from 'react'
import { toast } from 'sonner'
import { api, call } from '@/api/client'
import { useMe } from '@/api/queries'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle, Input } from '@/components/ui/primitives'
import { t } from '@/i18n'

/**
 * The second broker of the hornets (spec 4.5): where they find the city broker when this hive does not answer. The address is the
 * one the HORNETS reach (for example through the router's WireGuard tunnel), not the one this hive uses. Empty: no second broker.
 */
export function FailoverCard() {
  const me = useMe()
  const qc = useQueryClient()
  const isAdmin = me.data?.roles.includes('admin') ?? false
  const state = useQuery({ queryKey: ['failover'], queryFn: () => call(api.GET('/api/failover')), enabled: isAdmin })
  const [host, setHost] = useState('')
  const [port, setPort] = useState('1883')
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    if (!state.data) return
    setHost(state.data.host ?? '')
    setPort(String(state.data.port))
  }, [state.data])

  if (!isAdmin) return null

  const portNumber = Number(port)
  const valid = host.trim() === '' || (Number.isInteger(portNumber) && portNumber >= 1 && portNumber <= 65535)

  async function save(e: FormEvent) {
    e.preventDefault()
    setBusy(true)
    try {
      const { response } = await api.PUT('/api/failover', { body: { host: host.trim() || null, port: portNumber || 1883 } })
      if (!response.ok) {
        toast.error(t('failover.bad'))
        return
      }
      toast.success(t('failover.saved'))
      await qc.invalidateQueries({ queryKey: ['failover'] })
    } finally {
      setBusy(false)
    }
  }

  return (
    <Card>
      <CardHeader><CardTitle>{t('failover.title')}</CardTitle></CardHeader>
      <CardContent className="space-y-3 text-sm">
        <p className="text-xs text-neutral-500">{t('failover.hint')}</p>
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
          <div className="col-span-2 flex items-center gap-3">
            <Button type="submit" disabled={busy || !valid}>{t('common.save')}</Button>
            {host.trim() === '' && <span className="text-xs text-neutral-500">{t('failover.off')}</span>}
          </div>
        </form>
      </CardContent>
    </Card>
  )
}
