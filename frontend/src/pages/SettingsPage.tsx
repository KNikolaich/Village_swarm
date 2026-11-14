import { Copy, Download, LogOut, Send, Trash2 } from 'lucide-react'
import QRCode from 'qrcode'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useLocation, useSearchParams } from 'react-router'
import { useEffect, useState, type FormEvent, type ReactNode } from 'react'
import { toast } from 'sonner'
import { api, call, errorText } from '@/api/client'
import { keys, useCluster, useMe, useTelegram } from '@/api/queries'
import { BackupCard } from '@/components/BackupCard'
import { CertificateCard } from '@/components/CertificateCard'
import { ConfigCard } from '@/components/ConfigCard'
import { PasswordInput } from '@/components/PasswordInput'
import { SecretsCard } from '@/components/SecretsCard'
import { RetentionCard } from '@/components/RetentionCard'
import { FailoverCard } from '@/components/FailoverCard'
import { WeatherSettingsCard } from '@/components/WeatherSettingsCard'
import { WifiRolloutPanel } from '@/components/WifiRolloutPanel'
import { HelpTip } from '@/components/HelpTip'
import { Button } from '@/components/ui/button'
import { Badge, Card, CardContent, CardHeader, CardTitle, Input, Select, Skeleton } from '@/components/ui/primitives'
import { getLanguage, t, type Language } from '@/i18n'
import { formatAgo } from '@/lib/format'
import { install, isInstalled, isIos, useInstallOffer } from '@/lib/install'

const groups = ['account', 'hive', 'links', 'maintenance'] as const
type SettingsGroup = (typeof groups)[number]

/** Settings in four groups, one tab each (kept in the address, ?tab=hive): my account, the hive itself, links and access, data and maintenance. */
export function SettingsPage() {
  const [params, setParams] = useSearchParams()
  const { hash } = useLocation()
  const asked = params.get('tab')
  const tab: SettingsGroup = groups.find((g) => g === asked) ?? 'account'

  // A link to a card on another tab ("#restart") opens that tab, and the card scrolls into view once it is on the page.
  useEffect(() => {
    if (hash) document.getElementById(hash.slice(1))?.scrollIntoView({ block: 'start' })
  }, [hash, tab])

  return (
    <div className="space-y-4">
      <h1 className="text-lg font-semibold">{t('settings.title')}</h1>
      <div role="tablist" className="sticky top-14 z-10 -mx-1 flex flex-wrap gap-2 bg-neutral-950/90 px-1 py-2 backdrop-blur">
        {groups.map((g) => (
          <button
            key={g}
            type="button"
            role="tab"
            aria-selected={tab === g}
            onClick={() => setParams(g === 'account' ? {} : { tab: g }, { replace: true })}
            className={
              tab === g
                ? 'rounded-full border border-amber-400 bg-amber-400/10 px-3 py-1 text-xs text-amber-400'
                : 'rounded-full border border-neutral-700 px-3 py-1 text-xs text-neutral-300 hover:border-amber-400 hover:text-amber-400'
            }
          >
            {t(`settings.groups.${g}`)}
          </button>
        ))}
      </div>

      {tab === 'account' && (
        <Group id="account">
          <TelegramCard />
          <TotpCard />
          <PasswordCard />
          <EmailCard />
          <DevicesCard />
          <InstallCard />
          <LogoutButton />
        </Group>
      )}

      {tab === 'hive' && (
        <Group id="hive">
          <LanguageCard />
          <AliasCard />
          <ConfigCard except={['backup']} />
          <WeatherSettingsCard />
          <WifiCard />
        </Group>
      )}

      {tab === 'links' && (
        <Group id="links">
          <FailoverCard />
          <CertificateCard />
          <MonitorCard />
        </Group>
      )}

      {tab === 'maintenance' && (
        <Group id="maintenance">
          <BackupCard />
          <ConfigCard only={['backup']} />
          <RetentionCard />
          <SecretsCard />
          <RestartCard />
        </Group>
      )}
      <HiveVersion />
    </div>
  )
}

/** One tab's cards. */
function Group({ id, children }: { id: string; children: ReactNode }) {
  return <section id={`settings-${id}`} className="space-y-4">{children}</section>
}

/** Ends the session; at the bottom of the account tab, so it does not sit on every screen. */
function LogoutButton() {
  const qc = useQueryClient()

  async function logout() {
    await api.POST('/api/auth/logout')
    // Drop everything the previous user saw, but keep the "me" query: App shows the login page when it is null.
    qc.removeQueries({ predicate: (q) => q.queryKey[0] !== keys.me[0] })
    qc.setQueryData(keys.me, null)
  }

  return (
    <Button variant="outline" className="w-full sm:w-auto" onClick={() => void logout()}>
      <LogOut />{t('nav.logout')}
    </Button>
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
      <CardHeader><CardTitle className="flex items-center gap-1">{t('settings.monitor')}<HelpTip text={t('settings.monitorHint')} /></CardTitle></CardHeader>
      <CardContent className="space-y-2">
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
    } catch (e) {
      toast.error(errorText(e, t('errors.generic')))
    }
  }

  return (
    <Card>
      <CardHeader><CardTitle className="flex items-center gap-1">{t('settings.wifi')}<HelpTip text={t('settings.wifiHint')} /></CardTitle></CardHeader>
      <CardContent>
        <form onSubmit={save} className="space-y-2">
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
    } catch (e) {
      toast.error(errorText(e, t('errors.generic')))
      setSaving(false)
    }
  }

  return (
    <Card>
      <CardHeader><CardTitle className="flex items-center gap-1">{t('settings.language')}<HelpTip text={t('settings.languageHint')} /></CardTitle></CardHeader>
      <CardContent className="space-y-2">
        <Select value={getLanguage()} disabled={!isAdmin || saving} onChange={(e) => void change(e.target.value as Language)} className="h-11 w-full max-w-xs">
          <option value="ru">Русский</option>
          <option value="en">English</option>
        </Select>
      </CardContent>
    </Card>
  )
}

/** Restarts the service (settings that are read at start): the page waits until the hive answers again, then reloads. */
function RestartCard() {
  const me = useMe()
  const [state, setState] = useState<'idle' | 'restarting'>('idle')
  if (!(me.data?.roles.includes('admin') ?? false)) return null

  async function restart() {
    if (!window.confirm(t('settings.restartConfirm'))) return
    try {
      await call(api.POST('/api/settings/restart'))
    } catch (e) {
      toast.error(errorText(e, t('errors.generic')))
      return
    }
    setState('restarting')
    // The hive goes down within a second; wait until it answers again.
    await new Promise((r) => setTimeout(r, 3000))
    for (let i = 0; i < 90; i++) {
      try {
        const response = await fetch('/api/settings/version', { cache: 'no-store' })
        if (response.ok) {
          window.location.reload()
          return
        }
      } catch {
        // still down
      }
      await new Promise((r) => setTimeout(r, 2000))
    }
    setState('idle')
    toast.error(t('settings.restartLate'))
  }

  return (
    <Card id="restart" className="scroll-mt-14">
      <CardHeader><CardTitle className="flex items-center gap-1">{t('settings.restart')}<HelpTip text={t('settings.restartHint')} /></CardTitle></CardHeader>
      <CardContent className="space-y-2">
        <Button variant="outline" disabled={state === 'restarting'} onClick={() => void restart()}>
          {state === 'restarting' ? t('settings.restarting') : t('settings.restartButton')}
        </Button>
      </CardContent>
    </Card>
  )
}

/** What this hive is called ("Дача", "Город", "Sbrbn | Home"): shown in the menu header and in the swarm instead of the word "Улей". */
function AliasCard() {
  const qc = useQueryClient()
  const me = useMe()
  const cluster = useCluster()
  const isAdmin = me.data?.roles.includes('admin') ?? false
  const [text, setText] = useState<string | null>(null)
  const shown = text ?? cluster.data?.alias ?? ''

  async function save() {
    try {
      await call(api.PUT('/api/settings/alias', { body: { alias: shown.trim() } }))
      setText(null)
      await qc.invalidateQueries({ queryKey: ['cluster'] })
      toast.success(t('settings.aliasSaved'))
    } catch (e) {
      toast.error(errorText(e, t('errors.generic')))
    }
  }

  return (
    <Card>
      <CardHeader><CardTitle className="flex items-center gap-1">{t('settings.alias')}<HelpTip text={t('settings.aliasHint')} /></CardTitle></CardHeader>
      <CardContent className="space-y-2">
        <form onSubmit={(e) => { e.preventDefault(); void save() }} className="flex max-w-sm gap-2">
          <Input value={shown} maxLength={40} disabled={!isAdmin} onChange={(e) => setText(e.target.value)} aria-label={t('settings.alias')} />
          <Button type="submit" disabled={!isAdmin || text == null}>{t('settings.save')}</Button>
        </form>
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
      <CardHeader><CardTitle className="flex items-center gap-1">{t('settings.email')}<HelpTip text={t('settings.emailHint')} /></CardTitle></CardHeader>
      <CardContent className="space-y-2">
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
      <CardHeader><CardTitle className="flex items-center gap-1">{t('settings.devices')}<HelpTip text={t('settings.devicesHint')} /></CardTitle></CardHeader>
      <CardContent className="space-y-2">
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
  // Already running as an app: nothing to offer.
  if (isInstalled()) return null

  return (
    <Card>
      <CardHeader><CardTitle>{t('settings.install')}</CardTitle></CardHeader>
      <CardContent className="space-y-2">
        {offer ? (
          <Button onClick={() => void install()}><Download />{t('settings.installButton')}</Button>
        ) : (
          <p className="text-sm text-neutral-400">{isIos() ? t('settings.installIos') : t('settings.installManual')}</p>
        )}
        {!window.isSecureContext && <p className="text-sm text-amber-300">{t('settings.installHttps')}</p>}
      </CardContent>
    </Card>
  )
}
