import { Copy, Download, Send, Trash2 } from 'lucide-react'
import QRCode from 'qrcode'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router'
import { useEffect, useState, type FormEvent } from 'react'
import { toast } from 'sonner'
import { api, call } from '@/api/client'
import { keys, useMe, useTelegram } from '@/api/queries'
import { BackupCard } from '@/components/BackupCard'
import { CertificateCard } from '@/components/CertificateCard'
import { PasswordInput } from '@/components/PasswordInput'
import { RetentionCard } from '@/components/RetentionCard'
import { WeatherSettingsCard } from '@/components/WeatherSettingsCard'
import { WifiRolloutPanel } from '@/components/WifiRolloutPanel'
import { Button } from '@/components/ui/button'
import { Badge, Card, CardContent, CardHeader, CardTitle, Input, Select, Skeleton } from '@/components/ui/primitives'
import { getLanguage, t, type Language } from '@/i18n'
import { formatAgo } from '@/lib/format'
import { install, isInstalled, isIos, useInstallOffer } from '@/lib/install'

/** Own account: Telegram link (spec 9.4), TOTP (spec 12), password, recovery email, trusted devices; the app install. */
export function SettingsPage() {
  return (
    <div className="space-y-4">
      <h1 className="text-lg font-semibold">{t('settings.title')}</h1>
      <TelegramCard />
      <TotpCard />
      <PasswordCard />
      <EmailCard />
      <DevicesCard />
      <InstallCard />
      <WifiCard />
      <WeatherSettingsCard />
      <LanguageCard />
      <BackupCard />
      <RetentionCard />
      <CertificateCard />
      <MonitorCard />
      <HiveVersion />
    </div>
  )
}

/** Which hive build is running (the git commit it was built from), to tell whether a deploy went through. */
function HiveVersion() {
  const version = useQuery({ queryKey: ['settings', 'version'], queryFn: () => call(api.GET('/api/settings/version')), staleTime: 60_000 })
  if (!version.data) return null
  return <p className="text-center text-xs text-neutral-500">{t('settings.version').replace('{version}', version.data.version)}</p>
}

/** The live MQTT window (admins only): read-only, no broker login needed. */
function MonitorCard() {
  const me = useMe()
  if (!(me.data?.roles.includes('admin') ?? false)) return null
  return (
    <Card>
      <CardHeader><CardTitle>{t('settings.monitor')}</CardTitle></CardHeader>
      <CardContent className="space-y-2">
        <p className="text-xs text-neutral-500">{t('settings.monitorHint')}</p>
        <Link to="/monitor" className="text-amber-400">{t('settings.monitorOpen')}</Link>
      </CardContent>
    </Card>
  )
}

/** Wi-Fi and the hive address for hornets: entered once here, filled in by the setup wizard (admins only). */
function WifiCard() {
  const qc = useQueryClient()
  const wifi = useQuery({ queryKey: ['settings', 'wifi'], queryFn: () => call(api.GET('/api/settings/wifi')) })
  const [ssid, setSsid] = useState('')
  const [password, setPassword] = useState('')
  const [hiveUrl, setHiveUrl] = useState('')
  useEffect(() => {
    if (wifi.data) {
      setSsid(wifi.data.ssid ?? '')
      setHiveUrl(wifi.data.hiveUrl ?? '')
    }
  }, [wifi.data])

  async function save(e: FormEvent) {
    e.preventDefault()
    try {
      await call(api.PUT('/api/settings/wifi', { body: { ssid, password: password || null, hiveUrl } }))
      setPassword('')
      toast.success(t('settings.saved'))
      await qc.invalidateQueries({ queryKey: ['settings', 'wifi'] })
    } catch {
      toast.error(t('errors.generic'))
    }
  }

  return (
    <Card>
      <CardHeader><CardTitle>{t('settings.wifi')}</CardTitle></CardHeader>
      <CardContent>
        <form onSubmit={save} className="space-y-2">
          <p className="text-xs text-neutral-500">{t('settings.wifiHint')}</p>
          <Input placeholder={t('add.ssid')} value={ssid} onChange={(e) => setSsid(e.target.value)} />
          <PasswordInput placeholder={wifi.data?.passwordSet ? t('settings.wifiPasswordKept') : t('add.pass')} value={password} onChange={(e) => setPassword(e.target.value)} />
          <Input placeholder="http://192.168.10.4" value={hiveUrl} onChange={(e) => setHiveUrl(e.target.value)} />
          <Button type="submit" disabled={!ssid.trim() || !hiveUrl.trim()}>{t('settings.save')}</Button>
        </form>
        {wifi.data?.ssid && wifi.data.passwordSet && <WifiRolloutPanel ssid={wifi.data.ssid} />}
      </CardContent>
    </Card>
  )
}

/** One language for the whole hive (admins only); the page reloads so every string switches at once. */
function LanguageCard() {
  const me = useMe()
  const isAdmin = me.data?.roles.includes('admin') ?? false
  const [saving, setSaving] = useState(false)

  async function change(language: Language) {
    setSaving(true)
    try {
      await call(api.PUT('/api/settings/language', { body: { language } }))
      window.location.reload()
    } catch {
      toast.error(t('errors.generic'))
      setSaving(false)
    }
  }

  return (
    <Card>
      <CardHeader><CardTitle>{t('settings.language')}</CardTitle></CardHeader>
      <CardContent className="space-y-2">
        <p className="text-xs text-neutral-500">{t('settings.languageHint')}</p>
        <Select value={getLanguage()} disabled={!isAdmin || saving} onChange={(e) => void change(e.target.value as Language)} className="h-11 w-full max-w-xs">
          <option value="ru">Русский</option>
          <option value="en">English</option>
        </Select>
      </CardContent>
    </Card>
  )
}

function TelegramCard() {
  const qc = useQueryClient()
  const tg = useTelegram()
  const [code, setCode] = useState<{ code: string; bot?: string | null } | null>(null)

  async function getCode() {
    const result = await call(api.POST('/api/me/telegram/code'))
    setCode({ code: result.code, bot: result.botUsername })
  }

  async function unlink(id: number) {
    await call(api.DELETE('/api/me/telegram/{id}', { params: { path: { id } } }))
    await qc.invalidateQueries({ queryKey: ['telegram'] })
  }

  // Poll while a code is shown, so the new chat appears as soon as /link is sent.
  useEffect(() => {
    if (!code) return
    const timer = setInterval(() => void qc.invalidateQueries({ queryKey: ['telegram'] }), 3000)
    return () => clearInterval(timer)
  }, [code, qc])

  const command = code ? `/link ${code.code}` : ''

  return (
    <Card>
      <CardHeader><CardTitle>{t('settings.telegram')}</CardTitle></CardHeader>
      <CardContent className="space-y-3">
        {tg.isLoading && <Skeleton className="h-16" />}
        {tg.data && !tg.data.enabled && <p className="text-sm text-amber-300">{t('settings.telegramOff')}</p>}
        {tg.data?.enabled && (
          <>
            <div>
              <p className="mb-1 text-sm text-neutral-400">{t('settings.telegramLinked')}</p>
              {tg.data.links.length === 0 && <p className="text-sm text-neutral-500">{t('settings.telegramNone')}</p>}
              <ul className="divide-y divide-neutral-800">
                {tg.data.links.map((l) => (
                  <li key={l.id} className="flex items-center justify-between py-2 text-sm">
                    <span>{l.username ? `@${l.username}` : l.chatId} <span className="text-neutral-500">· {formatAgo(l.linkedAt)}</span></span>
                    <Button variant="ghost" size="sm" onClick={() => void unlink(l.id)}><Trash2 />{t('settings.unlink')}</Button>
                  </li>
                ))}
              </ul>
            </div>
            {code ? (
              <div className="space-y-2 rounded-lg bg-neutral-800/60 p-3">
                <p className="text-sm text-neutral-300">{t('settings.codeHint')}</p>
                <div className="flex items-center gap-2">
                  <code className="flex-1 rounded bg-neutral-950 px-3 py-2 text-lg tracking-wider text-amber-300">{command}</code>
                  <Button variant="secondary" size="icon" onClick={() => void navigator.clipboard.writeText(command).then(() => toast.success(t('common.copied')))}><Copy /></Button>
                </div>
                {code.bot && (
                  <a className="inline-flex items-center gap-1 text-sm text-amber-400" href={`https://t.me/${code.bot}`} target="_blank" rel="noreferrer">
                    <Send className="size-4" /> @{code.bot}
                  </a>
                )}
              </div>
            ) : (
              <Button onClick={() => void getCode()}>{t('settings.getCode')}</Button>
            )}
          </>
        )}
      </CardContent>
    </Card>
  )
}

function TotpCard() {
  const qc = useQueryClient()
  const me = useMe()
  const [setup, setSetup] = useState<{ key: string; qr: string } | null>(null)
  const [code, setCode] = useState('')
  const [recovery, setRecovery] = useState<string[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const enabled = me.data?.totpEnabled ?? false

  async function start() {
    const s = await call(api.POST('/api/me/totp/setup'))
    setSetup({ key: s.sharedKey, qr: await QRCode.toDataURL(s.authenticatorUri, { margin: 1, width: 220 }) })
  }

  async function submit(e: FormEvent) {
    e.preventDefault()
    setError(null)
    const { data, response } = enabled
      ? await api.POST('/api/me/totp/disable', { body: { code } })
      : await api.POST('/api/me/totp/enable', { body: { code } })
    if (!response.ok) return setError(t('settings.wrongCode'))
    if (data && 'recoveryCodes' in data) setRecovery(data.recoveryCodes)
    setSetup(null)
    setCode('')
    await qc.invalidateQueries({ queryKey: keys.me })
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t('settings.totp')}</CardTitle>
        <Badge tone={enabled ? 'ok' : 'neutral'}>{enabled ? t('settings.totpOn') : t('settings.totpOff')}</Badge>
      </CardHeader>
      <CardContent className="space-y-3">
        {recovery && (
          <div className="rounded-lg bg-neutral-800/60 p-3 text-sm">
            <p className="mb-2 text-amber-300">{t('settings.recovery')}</p>
            <div className="grid grid-cols-2 gap-1 font-mono text-neutral-200">{recovery.map((r) => <span key={r}>{r}</span>)}</div>
          </div>
        )}
        {!enabled && !setup && <Button variant="secondary" onClick={() => void start()}>{t('settings.totpSetup')}</Button>}
        {setup && (
          <div className="space-y-2">
            <p className="text-sm text-neutral-400">{t('settings.totpScan')}</p>
            <img src={setup.qr} alt="QR" className="rounded-lg bg-white p-2" width={220} height={220} />
            <code className="block break-all text-xs text-neutral-500">{setup.key}</code>
          </div>
        )}
        {(setup || enabled) && (
          <form onSubmit={submit} className="flex gap-2">
            <Input inputMode="numeric" autoComplete="one-time-code" placeholder={t('settings.totpCodeHint')} value={code} onChange={(e) => setCode(e.target.value)} className="max-w-40" />
            <Button type="submit" variant={enabled ? 'outline' : 'default'} disabled={code.length < 6}>
              {enabled ? t('settings.totpDisable') : t('settings.totpEnable')}
            </Button>
          </form>
        )}
        {error && <p className="text-sm text-red-400">{error}</p>}
      </CardContent>
    </Card>
  )
}

function PasswordCard() {
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [busy, setBusy] = useState(false)

  async function submit(e: FormEvent) {
    e.preventDefault()
    setBusy(true)
    const { response, error } = await api.POST('/api/me/password', { body: { currentPassword: current, newPassword: next } })
    setBusy(false)
    if (response.ok) {
      toast.success(t('settings.saved'))
      setCurrent('')
      setNext('')
    } else {
      const errors = (error as { errors?: Record<string, string[]> } | undefined)?.errors
      toast.error(errors ? Object.values(errors).flat().join(' ') : t('errors.generic'))
    }
  }

  return (
    <Card>
      <CardHeader><CardTitle>{t('settings.password')}</CardTitle></CardHeader>
      <CardContent>
        <form onSubmit={submit} className="space-y-2">
          <PasswordInput autoComplete="current-password" placeholder={t('settings.currentPassword')} value={current} onChange={(e) => setCurrent(e.target.value)} />
          <PasswordInput autoComplete="new-password" placeholder={t('settings.newPassword')} value={next} onChange={(e) => setNext(e.target.value)} />
          <Button type="submit" variant="secondary" disabled={busy || !current || next.length < 10}>{t('settings.save')}</Button>
        </form>
      </CardContent>
    </Card>
  )
}

function EmailCard() {
  const qc = useQueryClient()
  const me = useMe()
  const options = useQuery({ queryKey: ['auth', 'options'], queryFn: async () => (await api.GET('/api/auth/options')).data ?? null, staleTime: Infinity })
  const [email, setEmail] = useState<string | null>(null)
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState(false)
  const value = email ?? me.data?.email ?? ''

  async function submit(e: FormEvent) {
    e.preventDefault()
    setBusy(true)
    const { response, error } = await api.PUT('/api/me/email', { body: { email: value || null, currentPassword: password } })
    setBusy(false)
    if (response.ok) {
      toast.success(value && options.data?.recoveryByEmail ? t('settings.emailSaved') : t('settings.saved'))
      setPassword('')
      setEmail(null)
      await qc.invalidateQueries({ queryKey: keys.me })
    } else {
      const errors = (error as { errors?: Record<string, string[]> } | undefined)?.errors
      toast.error(errors ? Object.values(errors).flat().join(' ') : t('errors.generic'))
    }
  }

  return (
    <Card>
      <CardHeader><CardTitle>{t('settings.email')}</CardTitle></CardHeader>
      <CardContent className="space-y-2">
        <p className="text-sm text-neutral-400">{t('settings.emailHint')}</p>
        {options.data && !options.data.recoveryByEmail && <p className="text-sm text-amber-300">{t('settings.emailOff')}</p>}
        <form onSubmit={submit} className="space-y-2">
          <Input type="email" autoComplete="email" placeholder="name@example.org" value={value} onChange={(e) => setEmail(e.target.value)} />
          <PasswordInput autoComplete="current-password" placeholder={t('settings.currentPassword')} value={password} onChange={(e) => setPassword(e.target.value)} />
          <Button type="submit" variant="secondary" disabled={busy || !password}>{t('settings.save')}</Button>
        </form>
      </CardContent>
    </Card>
  )
}

function DevicesCard() {
  const qc = useQueryClient()
  const devices = useQuery({ queryKey: ['me', 'devices'], queryFn: async () => call(api.GET('/api/me/devices')) })

  async function forget(id: number) {
    await call(api.DELETE('/api/me/devices/{id}', { params: { path: { id } } }))
    await qc.invalidateQueries({ queryKey: ['me', 'devices'] })
  }

  return (
    <Card>
      <CardHeader><CardTitle>{t('settings.devices')}</CardTitle></CardHeader>
      <CardContent className="space-y-2">
        <p className="text-sm text-neutral-400">{t('settings.devicesHint')}</p>
        {devices.isLoading && <Skeleton className="h-12" />}
        {devices.data?.length === 0 && <p className="text-sm text-neutral-500">{t('settings.devicesNone')}</p>}
        <ul className="divide-y divide-neutral-800">
          {devices.data?.map((d) => (
            <li key={d.id} className="flex items-center justify-between gap-2 py-2 text-sm">
              <span className="min-w-0">
                <span className="block truncate">
                  {d.name ?? '—'} {d.current && <Badge tone="ok">{t('settings.deviceCurrent')}</Badge>}
                </span>
                <span className="text-neutral-500">{formatAgo(d.lastSeenAt)}{d.lastIp ? ` · ${d.lastIp}` : ''}</span>
              </span>
              <Button variant="ghost" size="sm" onClick={() => void forget(d.id)}><Trash2 />{t('settings.deviceForget')}</Button>
            </li>
          ))}
        </ul>
      </CardContent>
    </Card>
  )
}

function InstallCard() {
  const offer = useInstallOffer()
  const installed = isInstalled()

  return (
    <Card>
      <CardHeader><CardTitle>{t('settings.install')}</CardTitle></CardHeader>
      <CardContent className="space-y-2">
        {installed ? (
          <p className="text-sm text-neutral-400">{t('settings.installDone')}</p>
        ) : offer ? (
          <Button onClick={() => void install()}><Download />{t('settings.installButton')}</Button>
        ) : (
          <p className="text-sm text-neutral-400">{isIos() ? t('settings.installIos') : t('settings.installManual')}</p>
        )}
        {!installed && !window.isSecureContext && <p className="text-sm text-amber-300">{t('settings.installHttps')}</p>}
      </CardContent>
    </Card>
  )
}
