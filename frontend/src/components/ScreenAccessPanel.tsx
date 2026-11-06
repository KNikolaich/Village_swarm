import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Copy, KeyRound, ShieldOff } from 'lucide-react'
import { useState } from 'react'
import { toast } from 'sonner'
import { api, call } from '@/api/client'
import { HelpTip } from '@/components/HelpTip'
import { PhoneSetup } from '@/components/PhoneSetup'
import { Button } from '@/components/ui/button'
import { t } from '@/i18n'
import { formatAgo } from '@/lib/format'
import { publicHiveUrl } from '@/lib/publicUrl'

/**
 * Access of a monitor to its own screen from anywhere (contracts/monitor-http.md): a read-only token issued here and shown
 * once. With it a panel at another place reads the same texts the screen gets at home, over the hive's internet address.
 */
export function ScreenAccessPanel({ deviceId }: { deviceId: string }) {
  const qc = useQueryClient()
  const key = ['screen-access', deviceId]
  const access = useQuery({
    queryKey: key,
    queryFn: () => call(api.GET('/api/devices/{deviceId}/screen-access', { params: { path: { deviceId } } })),
  })
  const [token, setToken] = useState<string | null>(null)
  const a = access.data
  const url = `${window.location.origin}${a?.path ?? '/api/monitor/v1/frame'}`

  async function issue() {
    if (a?.enabled && !window.confirm(t('screenAccess.replaceConfirm'))) return
    const { data, response } = await api.POST('/api/devices/{deviceId}/screen-access', { params: { path: { deviceId } } })
    if (!response.ok || !data) {
      toast.error(t('errors.generic'))
      return
    }
    setToken(data.token)
    await qc.invalidateQueries({ queryKey: key })
  }

  async function revoke() {
    if (!window.confirm(t('screenAccess.revokeConfirm'))) return
    const { response } = await api.DELETE('/api/devices/{deviceId}/screen-access', { params: { path: { deviceId } } })
    if (!response.ok) {
      toast.error(t('errors.generic'))
      return
    }
    setToken(null)
    toast.success(t('screenAccess.revoked'))
    await qc.invalidateQueries({ queryKey: key })
  }

  const copy = (text: string) => void navigator.clipboard.writeText(text).then(() => toast.success(t('common.copied')))

  return (
    <div className="space-y-3 text-sm">
      <p className="flex items-start gap-1 text-xs text-neutral-500">
        <span>{t('screenAccess.hint')}</span>
        <HelpTip text={t('screenAccess.help')} />
      </p>
      {a && (
        <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-xs">
          <dt className="text-neutral-400">{t('screenAccess.state')}</dt>
          <dd className={a.enabled ? 'text-emerald-400' : 'text-neutral-300'}>
            {a.enabled ? t('screenAccess.open').replace('{when}', formatAgo(a.issuedAt)) : t('screenAccess.closed')}
          </dd>
          {a.enabled && (
            <>
              <dt className="text-neutral-400">{t('screenAccess.lastRead')}</dt>
              <dd className="text-neutral-300">
                {a.lastReadAt
                  ? `${formatAgo(a.lastReadAt)} · ${a.lastReadFrom ?? '?'} · ${t(a.lastReadExternal ? 'screenAccess.fromInternet' : 'screenAccess.fromHome')}`
                  : t('screenAccess.never')}
              </dd>
            </>
          )}
        </dl>
      )}
      {token && (
        <div className="space-y-2 rounded-lg border border-amber-700/60 bg-amber-950/30 p-3 text-xs">
          <p className="text-amber-300">{t('screenAccess.once')}</p>
          <div className="flex items-center gap-2">
            <code className="min-w-0 flex-1 break-all rounded bg-neutral-900 px-2 py-1 text-neutral-200">{token}</code>
            <Button variant="secondary" size="icon" title={t('screenAccess.copyToken')} aria-label={t('screenAccess.copyToken')} onClick={() => copy(token)}><Copy /></Button>
          </div>
          {publicHiveUrl() && <PhoneSetup url={publicHiveUrl()} token={token} />}
          <p className="text-neutral-400">{t('screenAccess.how')}</p>
          <div className="flex items-center gap-2">
            <code className="min-w-0 flex-1 break-all rounded bg-neutral-900 px-2 py-1 text-neutral-300">GET {url}{'\n'}Authorization: Bearer …</code>
            <Button variant="secondary" size="icon" title={t('screenAccess.copyUrl')} aria-label={t('screenAccess.copyUrl')} onClick={() => copy(url)}><Copy /></Button>
          </div>
        </div>
      )}
      <div className="flex flex-wrap gap-3">
        <Button variant="outline" onClick={() => void issue()}><KeyRound />{t(a?.enabled ? 'screenAccess.reissue' : 'screenAccess.issue')}</Button>
        {a?.enabled && <Button variant="outline" onClick={() => void revoke()}><ShieldOff />{t('screenAccess.revoke')}</Button>}
      </div>
    </div>
  )
}
