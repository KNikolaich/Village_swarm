import { useEffect, useRef, useState } from 'react'
import { api, call, errorText } from '@/api/client'
import { Button } from '@/components/ui/button'
import { t } from '@/i18n'

type Result = { ok: boolean; detail: string }

/**
 * "Check" next to the off-site address: the backup container really tries it (folder exists and is writable, share answers,
 * the password fits, free space) and the answer shows here. It tests the text in the field, so it works before saving.
 */
export function RepositoryCheck({ address }: { address: string }) {
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState<Result | null>(null)
  const cancelled = useRef(false)
  useEffect(() => () => { cancelled.current = true }, [])

  async function check() {
    setBusy(true)
    setResult(null)
    try {
      const { requestId } = await call(api.POST('/api/backups/check', { body: { repository: address.trim() } }))
      // The container looks for requests every few seconds; a slow network share can take up to a minute.
      for (let i = 0; i < 45 && !cancelled.current; i++) {
        await new Promise((r) => setTimeout(r, 2000))
        const state = await call(api.GET('/api/backups/check'))
        if (state.requestId === requestId && state.done) {
          setResult({ ok: state.ok, detail: state.detail ?? '' })
          return
        }
      }
      if (!cancelled.current) setResult({ ok: false, detail: t('config.check.noAnswer') })
    } catch (e) {
      setResult({ ok: false, detail: errorText(e, t('config.check.badAddress')) })
    } finally {
      if (!cancelled.current) setBusy(false)
    }
  }

  return (
    <div className="space-y-1">
      <Button variant="outline" size="sm" disabled={busy} onClick={() => void check()}>{busy ? t('config.check.busy') : t('config.check.button')}</Button>
      {result && (
        <p role="status" className={result.ok ? 'text-xs text-emerald-400' : 'text-xs text-red-400'}>
          {/* The container writes its findings in English; shown as they are. */}
          {result.ok ? '✓ ' : '✗ '}{result.detail}
        </p>
      )}
    </div>
  )
}
