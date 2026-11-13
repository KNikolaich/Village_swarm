import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { ApiError, api, call } from '@/api/client'
import { useMe } from '@/api/queries'
import { PasswordInput } from '@/components/PasswordInput'
import { Button } from '@/components/ui/button'
import { Badge, Card, CardContent, CardHeader, CardTitle, Skeleton } from '@/components/ui/primitives'
import { t } from '@/i18n'

const queryKey = ['settings', 'secrets'] as const

/**
 * Third-party secrets (bot token, LLM key, mail password) kept encrypted in the database instead of .env (admins only).
 * Write-only: the page only ever learns whether a secret is set. Read at start, so a restart applies them.
 */
export function SecretsCard() {
  const me = useMe()
  const isAdmin = me.data?.roles.includes('admin') ?? false
  const secrets = useQuery({ queryKey, queryFn: () => call(api.GET('/api/settings/secrets')), enabled: isAdmin })
  if (!isAdmin) return null
  return (
    <Card>
      <CardHeader><CardTitle>{t('secrets.title')}</CardTitle></CardHeader>
      <CardContent className="space-y-3 text-sm">
        <p className="text-xs text-neutral-500">{t('secrets.hint')}</p>
        {secrets.isLoading && <Skeleton className="h-24" />}
        {secrets.data?.secrets.map((s) => <SecretRow key={s.key} secretKey={s.key} source={s.source} />)}
        {secrets.data && (
          <p className="rounded-md border border-amber-500/40 bg-amber-500/10 p-2 text-xs text-amber-200">
            {t('secrets.keysWarning').replace('{path}', secrets.data.keysPath || 'HIVE_DATA/keys')}
          </p>
        )}
      </CardContent>
    </Card>
  )
}

function SecretRow({ secretKey, source }: { secretKey: string; source: string }) {
  const qc = useQueryClient()
  const [value, setValue] = useState('')
  const [busy, setBusy] = useState(false)

  async function save(next: string) {
    setBusy(true)
    try {
      await call(api.PUT('/api/settings/secrets/{key}', { params: { path: { key: secretKey } }, body: { value: next } }))
      setValue('')
      await qc.invalidateQueries({ queryKey })
      toast.success(t(next ? 'secrets.saved' : 'secrets.cleared'))
    } catch (e) {
      toast.error(e instanceof ApiError && e.status === 403 ? e.message : t('errors.generic'))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="space-y-1">
      <div className="flex items-center justify-between gap-2">
        <span>{t(`secrets.names.${secretKey}`)}</span>
        <Badge>{t(`secrets.source.${source}`)}</Badge>
      </div>
      <div className="flex gap-2">
        <div className="flex-1">
          <PasswordInput value={value} onChange={(e) => setValue(e.target.value)} autoComplete="off" placeholder={t('secrets.newValue')} aria-label={t(`secrets.names.${secretKey}`)} />
        </div>
        <Button disabled={busy || !value.trim()} onClick={() => void save(value)}>{t('secrets.save')}</Button>
        {source === 'db' || source === 'unreadable' ? (
          <Button variant="outline" disabled={busy} onClick={() => void save('')}>{t('secrets.clear')}</Button>
        ) : null}
      </div>
    </div>
  )
}
