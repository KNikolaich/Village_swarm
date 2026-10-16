import { Copy, Send, Trash2 } from 'lucide-react'
import QRCode from 'qrcode'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router'
import { useEffect, useState, type FormEvent } from 'react'
import { toast } from 'sonner'
import { api, call } from '@/api/client'
import { keys, useDevices, useMe, useTelegram } from '@/api/queries'
import { BackupCard } from '@/components/BackupCard'
import { CertificateCard } from '@/components/CertificateCard'
import { PasswordInput } from '@/components/PasswordInput'
import { RetentionCard } from '@/components/RetentionCard'
import { WeatherSettingsCard } from '@/components/WeatherSettingsCard'
import { Button } from '@/components/ui/button'
import { Badge, Card, CardContent, CardHeader, CardTitle, Input, Select, Skeleton } from '@/components/ui/primitives'
import { getLanguage, t, type Language } from '@/i18n'
import { formatAgo } from '@/lib/format'

/** Own account: Telegram link (spec 9.4), TOTP (spec 12), password. */
export function SettingsPage() {
  return (
    <div className="space-y-4">
      <h1 className="text-lg font-semibold">{t('settings.title')}</h1>
      <TelegramCard />
      <TotpCard />
      <PasswordCard />
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
  const [rollout, setRollout] = useState<{ deviceId: string; name: string; result: string }[] | null>(null)
  const [applying, setApplying] = useState(false)
  const devices = useDevices()
  const [chosen, setChosen] = useState<string[]>([])
  const online = (devices.data ?? []).filter((d) => d.status === 'online' && d.type !== 'hive')
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

  async function apply() {
    if (chosen.length === 0 || !window.confirm(t('settings.wifiApplyConfirm').replace('{ssid}', wifi.data?.ssid ?? ''))) return
    setApplying(true)
    try {
      setRollout(await call(api.POST('/api/settings/wifi/apply', { body: { deviceIds: chosen } })))
    } catch {
      toast.error(t('errors.generic'))
    } finally {
      setApplying(false)
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
        {wifi.data?.ssid && wifi.data.passwordSet && (
          <div className="mt-4 space-y-2 border-t border-neutral-800 pt-3">
            <p className="text-xs text-neutral-500">{t('settings.wifiApplyHint')}</p>
            <p className="rounded-lg border border-amber-900/60 bg-amber-950/20 p-2 text-xs text-amber-300">{t('settings.wifiApplyWarning')}</p>
            <p className="text-xs text-neutral-400">{t('settings.wifiApplyChoose')}</p>
            {online.length === 0 && <p className="text-xs text-neutral-500">{t('settings.wifiApplyNobody')}</p>}
            <ul className="space-y-1">
              {online.map((d) => (
                <li key={d.deviceId}>
                  <label className="flex cursor-pointer items-center gap-2 text-sm">
                    <input type="checkbox" className="size-4 accent-amber-400" checked={chosen.includes(d.deviceId)}
                      onChange={(e) => setChosen(e.target.checked ? [...chosen, d.deviceId] : chosen.filter((x) => x !== d.deviceId))} />
                    {d.name}
                  </label>
                </li>
              ))}
            </ul>
            <div className="flex flex-wrap items-center gap-3">
              <Button variant="outline" disabled={applying || chosen.length === 0} onClick={() => void apply()}>{chosen.length ? t('settings.wifiApplyGo') : t('settings.wifiApplyNeed')}</Button>
              <Link to="/console" className="text-xs text-amber-400">{t('settings.wifiUsbRescue')}</Link>
            </div>
            {rollout && (
              <ul className="space-y-1 text-sm">
                {rollout.map((r) => (
                  <li key={r.deviceId} className="flex items-center justify-between gap-3">
                    <span className="min-w-0 truncate">{r.name}</span>
                    <Badge tone={r.result === 'sent' ? 'ok' : r.result === 'failed' ? 'alarm' : 'neutral'}>{t(`settings.wifiResult.${r.result}`, r.result)}</Badge>
                  </li>
                ))}
                {rollout.length === 0 && <li className="text-neutral-500">{t('settings.wifiApplyNone')}</li>}
              </ul>
            )}
          </div>
        )}
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
