import { useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { api } from '@/api/client'
import { keys } from '@/api/queries'
import { HoneycombLogo } from '@/components/HoneycombLogo'
import { PasswordInput } from '@/components/PasswordInput'
import { Button } from '@/components/ui/button'
import { Card, CardContent, Input } from '@/components/ui/primitives'
import { t } from '@/i18n'

/** Password, then the TOTP code when the account has an authenticator app (spec 12). */
export function LoginPage() {
  const qc = useQueryClient()
  const [step, setStep] = useState<'password' | 'totp'>('password')
  const [login, setLogin] = useState('')
  const [password, setPassword] = useState('')
  const [code, setCode] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(e: FormEvent) {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const { data, response } =
        step === 'password'
          ? await api.POST('/api/auth/login', { body: { login, password, rememberMe: true } })
          : await api.POST('/api/auth/totp', { body: { code, rememberMe: true, rememberDevice: false } })
      if (response.status === 423) return setError(t('login.locked'))
      if (response.status >= 500) return setError(t('login.serverError'))
      if (!response.ok || !data) return setError(t(step === 'password' ? 'login.failed' : 'login.totpFailed'))
      if (data.status === 'totpRequired') {
        setStep('totp')
        return
      }
      await qc.invalidateQueries({ queryKey: keys.me })
    } catch (e) {
      // The browser could not complete the request at all: say what it reported, it differs between browsers
      // (certificate exception, blocked HTTP/3, offline) and is the only hint when the page looks fine.
      console.error('login request failed', e)
      setError(`${t('errors.network')}: ${e instanceof Error ? `${e.name}: ${e.message}` : String(e)}`)
    } finally {
      setBusy(false)
    }
  }

  return (
    <main className="flex min-h-dvh items-center justify-center p-4">
      <Card className="w-full max-w-sm">
        <CardContent className="space-y-6 p-6">
          <div className="flex items-center gap-3">
            <HoneycombLogo className="size-10" />
            <div>
              <h1 className="text-xl font-semibold">{step === 'password' ? t('login.title') : t('login.totpTitle')}</h1>
              <p className="text-sm text-neutral-500">{t('app.subtitle')}</p>
            </div>
          </div>
          <form onSubmit={submit} className="space-y-3">
            {step === 'password' ? (
              <>
                <Input autoFocus autoComplete="username" placeholder={t('login.login')} value={login} onChange={(e) => setLogin(e.target.value)} />
                <PasswordInput autoComplete="current-password" placeholder={t('login.password')} value={password} onChange={(e) => setPassword(e.target.value)} />
              </>
            ) : (
              <>
                <p className="text-sm text-neutral-400">{t('login.totpHint')}</p>
                <Input autoFocus inputMode="numeric" autoComplete="one-time-code" placeholder="123456" value={code} onChange={(e) => setCode(e.target.value)} />
              </>
            )}
            {error && <p className="text-sm text-red-400">{error}</p>}
            <Button type="submit" size="lg" className="w-full" disabled={busy || (step === 'password' ? !login || !password : !code)}>
              {t('login.submit')}
            </Button>
            {step === 'totp' && (
              <Button variant="ghost" className="w-full" onClick={() => { setStep('password'); setCode(''); setError(null) }}>
                {t('login.back')}
              </Button>
            )}
          </form>
        </CardContent>
      </Card>
    </main>
  )
}
