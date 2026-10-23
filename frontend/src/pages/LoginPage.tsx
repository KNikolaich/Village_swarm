import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { api } from '@/api/client'
import { keys } from '@/api/queries'
import { HoneycombLogo } from '@/components/HoneycombLogo'
import { PasswordInput } from '@/components/PasswordInput'
import { Button } from '@/components/ui/button'
import { Card, CardContent, Input } from '@/components/ui/primitives'
import { t } from '@/i18n'

type Step = 'password' | 'code' | 'totp' | 'recover' | 'recoverCode'

/**
 * Password, then on a new device the code the bot sent to Telegram (or the authenticator app's code) (spec 12).
 * A forgotten password: a recovery code to the account's email, typed here with the new password.
 */
export function LoginPage() {
  const qc = useQueryClient()
  const [step, setStep] = useState<Step>('password')
  const [login, setLogin] = useState('')
  const [password, setPassword] = useState('')
  const [code, setCode] = useState('')
  const [totpAllowed, setTotpAllowed] = useState(false)
  const [rememberDevice, setRememberDevice] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const options = useQuery({
    queryKey: ['auth', 'options'],
    queryFn: async () => (await api.GET('/api/auth/options')).data ?? null,
    staleTime: Infinity,
  })

  function go(next: Step) {
    setStep(next)
    setCode('')
    setError(null)
    if (next === 'password') setNotice(null)
  }

  function lockedText(body: unknown) {
    const until = (body as { lockedUntil?: string | null } | undefined)?.lockedUntil
    if (!until) return t('login.locked')
    const date = new Date(until)
    const sameDay = date.toDateString() === new Date().toDateString()
    const when = date.toLocaleString(undefined, sameDay ? { hour: '2-digit', minute: '2-digit' } : { day: 'numeric', month: 'long', hour: '2-digit', minute: '2-digit' })
    return t('login.lockedUntil').replace('{time}', when)
  }

  async function submit(e: FormEvent) {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      if (step === 'recover') {
        const { response, error } = await api.POST('/api/auth/recover', { body: { login } })
        if (response.status === 423) return setError(lockedText(error))
        if (!response.ok) return setError(t('login.serverError'))
        setNotice(t('login.recoverSent'))
        go('recoverCode')
        return
      }
      if (step === 'recoverCode') {
        const { response, error } = await api.POST('/api/auth/recover/confirm', { body: { login, code, newPassword: password } })
        if (response.status === 423) return setError(lockedText(error))
        if (response.status === 400) {
          const errors = (error as { errors?: Record<string, string[]> } | undefined)?.errors
          return setError(errors ? Object.values(errors).flat().join(' ') : t('errors.generic'))
        }
        if (!response.ok) return setError(t('login.recoverFailed'))
        setNotice(t('login.recoverDone'))
        setStep('password')
        setCode('')
        return
      }
      const { data, response, error } =
        step === 'password'
          ? await api.POST('/api/auth/login', { body: { login, password, rememberMe: true } })
          : await api.POST('/api/auth/totp', { body: { code, rememberMe: true, rememberDevice } })
      if (response.status === 423) return setError(lockedText(error))
      if (response.status >= 500) return setError(t('login.serverError'))
      if (!response.ok || !data) return setError(t(step === 'password' ? 'login.failed' : 'login.totpFailed'))
      if (data.status === 'codeRequired' || data.status === 'totpRequired') {
        setTotpAllowed(data.totpAllowed)
        go(data.status === 'codeRequired' ? 'code' : 'totp')
        return
      }
      if (data.status === 'secondFactorMissing') return setError(t('login.secondFactorMissing'))
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

  const title = {
    password: t('login.title'),
    code: t('login.codeTitle'),
    totp: t('login.totpTitle'),
    recover: t('login.recoverTitle'),
    recoverCode: t('login.recoverTitle'),
  }[step]
  const ready = {
    password: !!login && !!password,
    code: !!code,
    totp: !!code,
    recover: !!login,
    recoverCode: !!code && password.length >= 10,
  }[step]

  return (
    <main className="flex min-h-dvh items-center justify-center p-4">
      <Card className="w-full max-w-sm">
        <CardContent className="space-y-6 p-6">
          <div className="flex items-center gap-3">
            <HoneycombLogo className="size-10" />
            <div>
              <h1 className="text-xl font-semibold">{title}</h1>
              <p className="text-sm text-neutral-500">{t('app.subtitle')}</p>
            </div>
          </div>
          <form onSubmit={submit} className="space-y-3">
            {notice && <p className="text-sm text-emerald-400">{notice}</p>}
            {step === 'password' && (
              <>
                <Input autoFocus autoComplete="username" placeholder={t('login.login')} value={login} onChange={(e) => setLogin(e.target.value)} />
                <PasswordInput autoComplete="current-password" placeholder={t('login.password')} value={password} onChange={(e) => setPassword(e.target.value)} />
              </>
            )}
            {(step === 'code' || step === 'totp') && (
              <>
                <p className="text-sm text-neutral-400">{step === 'code' ? (totpAllowed ? t('login.codeHintTotp') : t('login.codeHint')) : t('login.totpHint')}</p>
                <Input autoFocus inputMode="numeric" autoComplete="one-time-code" placeholder="123456" value={code} onChange={(e) => setCode(e.target.value)} />
                <label className="flex items-center gap-2 text-sm text-neutral-400">
                  <input type="checkbox" className="accent-amber-500" checked={rememberDevice} onChange={(e) => setRememberDevice(e.target.checked)} />
                  {t('login.rememberDevice')}
                </label>
              </>
            )}
            {step === 'recover' && (
              <>
                <p className="text-sm text-neutral-400">{t('login.recoverHint')}</p>
                <Input autoFocus autoComplete="username" placeholder={t('login.login')} value={login} onChange={(e) => setLogin(e.target.value)} />
              </>
            )}
            {step === 'recoverCode' && (
              <>
                <Input autoFocus autoComplete="one-time-code" placeholder={t('login.recoverCode')} value={code} onChange={(e) => setCode(e.target.value)} />
                <PasswordInput autoComplete="new-password" placeholder={t('login.newPassword')} value={password} onChange={(e) => setPassword(e.target.value)} />
              </>
            )}
            {error && <p className="text-sm text-red-400">{error}</p>}
            <Button type="submit" size="lg" className="w-full" disabled={busy || !ready}>
              {step === 'recover' ? t('login.recoverSend') : step === 'recoverCode' ? t('login.recoverSave') : t('login.submit')}
            </Button>
            {step === 'password' && options.data?.recoveryByEmail && (
              <Button type="button" variant="ghost" className="w-full" onClick={() => { setPassword(''); go('recover') }}>
                {t('login.forgot')}
              </Button>
            )}
            {step !== 'password' && (
              <Button type="button" variant="ghost" className="w-full" onClick={() => { setPassword(''); go('password') }}>
                {t('login.back')}
              </Button>
            )}
          </form>
        </CardContent>
      </Card>
    </main>
  )
}
