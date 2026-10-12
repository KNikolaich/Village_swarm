import { useQuery } from '@tanstack/react-query'
import { CheckCircle2, Copy, Cpu, Download, Usb } from 'lucide-react'
import { createElement, useEffect, useRef, useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import { toast } from 'sonner'
import { api, call } from '@/api/client'
import { PasswordInput } from '@/components/PasswordInput'
import { Button } from '@/components/ui/button'
import { Badge, Card, CardContent, CardHeader, CardTitle, Input, Select } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { HornetSerial, serialSupported } from '@/lib/serial'

const types = [
  { code: 'guard-cam', title: 'Камера с датчиком движения' },
  { code: 'meteo', title: 'Метеодатчик' },
  { code: 'relay', title: 'Реле (свет, розетка)' },
  { code: 'heat', title: 'Обогрев' },
  { code: 'leak', title: 'Датчик протечки' },
  { code: 'plant', title: 'Растения' },
]

const idPattern = /^[a-z][a-z0-9]*(-[a-z0-9]+)*$/

// Board ids from firmware/dist/<build>/build.json (HORNET_BOARD) → what people call the board.
const boardTitles: Record<string, string> = {
  'esp32cam-aithinker': 'ESP32-CAM AI-Thinker',
  'esp32c3-mini': 'ESP32-C3 mini (SuperMini)',
  'esp32c3-oled-042': 'ESP32-C3 с экраном 0,42"',
  'wemos-d1-mini': 'Wemos D1 mini',
  'nodemcu-v3': 'NodeMCU v3',
  'esp-01': 'ESP-01 / ESP-01S (1 МБ)',
  'esp-01-dht11': 'ESP-01S с датчиком DHT11 (модуль «ESP-01S DHT11»)',
}
const boardTitle = (board: string) => boardTitles[board] ?? board

export function CodeBox({ code, expiresAt }: { code: string; expiresAt: string }) {
  return (
    <div className="flex items-center gap-2">
      <code className="flex-1 rounded bg-neutral-950 px-3 py-2 text-xl tracking-[0.2em] text-amber-300">{code}</code>
      <Button variant="secondary" size="icon" onClick={() => void navigator.clipboard.writeText(code).then(() => toast.success('Скопировано'))}><Copy /></Button>
      <span className="text-xs text-neutral-500">до {new Date(expiresAt).toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' })}</span>
    </div>
  )
}

/**
 * Hive address as the board sees it: the PC/RPi in the LAN, never localhost. On the hive the UI is HTTPS
 * but hornets use plain HTTP on port 80 (deploy/caddy/Caddyfile); the Vite dev server means the api on :5080.
 */
function defaultHiveUrl() {
  const { hostname } = window.location
  if (hostname === 'localhost' || hostname === '127.0.0.1') return ''
  return import.meta.env.DEV ? `http://${hostname}:5080` : `http://${hostname}`
}

/**
 * "Add hornet" wizard (spec 5.3): code → flash in the browser (ESP Web Tools) → Wi-Fi + code over Web Serial
 * → wait until the hornet is online. Every step also shows the manual way.
 */
export function AddHornetPage() {
  const [mode, setMode] = useState<'one' | 'batch'>('one')
  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <h1 className="text-lg font-semibold">{t('swarm.add')}</h1>
        <Link to="/swarm" className="text-sm text-amber-400">{t('swarm.title')}</Link>
      </div>
      <div className="flex gap-2">
        <Button variant={mode === 'one' ? 'default' : 'secondary'} onClick={() => setMode('one')}>{t('add.modeOne')}</Button>
        <Button variant={mode === 'batch' ? 'default' : 'secondary'} onClick={() => setMode('batch')}>{t('add.modeBatch')}</Button>
      </div>
      {mode === 'one' ? <OneHornet /> : <BatchPanel />}
    </div>
  )
}

/** Batch: N boards of one type, one queue on the server. Flash and set up each board in turn (USB, one at a time). */
function BatchPanel() {
  const [type, setType] = useState('meteo')
  const [idPrefix, setIdPrefix] = useState('')
  const [namePrefix, setNamePrefix] = useState('')
  const [count, setCount] = useState(3)
  const [items, setItems] = useState<{ deviceId: string; name: string; code: string; expiresAt: string }[]>([])
  const [active, setActive] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const firmware = useQuery({ queryKey: ['firmware'], queryFn: () => call(api.GET('/api/firmware')) })
  const builds = (firmware.data ?? []).filter((b) => b.role === type)
  const [build, setBuild] = useState('')
  const selectedBuild = builds.find((b) => b.build === build) ?? builds[0]

  async function create(e: FormEvent) {
    e.preventDefault()
    setError(null)
    const { data, error: err, response } = await api.POST('/api/provision/batches', { body: { type, idPrefix, namePrefix, count } })
    if (!response.ok || !data) {
      setError((err as { errors?: Record<string, string[]> } | undefined)?.errors?.request?.[0] ?? t('errors.generic'))
      return
    }
    setItems(data.items.map((c) => ({ deviceId: c.deviceId, name: c.name, code: c.code, expiresAt: c.expiresAt })))
    setActive(data.items[0]?.deviceId ?? null)
  }

  const idValid = /^[a-z][a-z0-9]*(-[a-z0-9]+)*-?$/.test(idPrefix) && idPrefix.length <= 28
  const current = items.find((i) => i.deviceId === active)

  return (
    <div className="space-y-4">
      {items.length === 0 && (
        <Card>
          <CardHeader><CardTitle>{t('add.batchTitle')}</CardTitle></CardHeader>
          <CardContent>
            <form onSubmit={create} className="space-y-2">
              <Select value={type} onChange={(e) => { setType(e.target.value); setBuild('') }} className="h-11 w-full">
                {types.map((x) => <option key={x.code} value={x.code}>{x.title}</option>)}
              </Select>
              <Input placeholder={t('add.batchIdPlaceholder')} value={idPrefix} onChange={(e) => setIdPrefix(e.target.value.toLowerCase())} />
              {idPrefix && !idValid && <p className="text-xs text-amber-300">{t('add.idHint')}</p>}
              <Input placeholder={t('add.batchNamePlaceholder')} value={namePrefix} onChange={(e) => setNamePrefix(e.target.value)} />
              <Input type="number" min={1} max={20} value={count} onChange={(e) => setCount(Number(e.target.value))} />
              {error && <p className="text-sm text-red-400">{error}</p>}
              <Button type="submit" disabled={!idValid || !namePrefix.trim() || count < 1 || count > 20}>{t('add.batchCreate')}</Button>
            </form>
          </CardContent>
        </Card>
      )}

      {items.length > 0 && (
        <Card>
          <CardHeader><CardTitle>{t('add.batchQueue')}</CardTitle><Button variant="ghost" size="sm" onClick={() => setItems([])}>{t('add.batchNew')}</Button></CardHeader>
          <CardContent className="space-y-2">
            <p className="text-xs text-neutral-500">{t('add.batchHint')}</p>
            {items.map((i) => (
              <div key={i.deviceId} className={`flex flex-wrap items-center justify-between gap-2 rounded-lg border p-2 ${i.deviceId === active ? 'border-amber-400' : 'border-neutral-800'}`}>
                <button type="button" className="min-w-0 text-left" onClick={() => setActive(i.deviceId)}>
                  <p className="truncate text-sm font-medium">{i.name}</p>
                  <p className="truncate text-xs text-neutral-500">{i.deviceId}</p>
                </button>
                <code className="text-sm tracking-widest text-amber-300">{i.code}</code>
              </div>
            ))}
          </CardContent>
        </Card>
      )}

      {current && (
        <>
          <Card>
            <CardHeader><CardTitle>{current.name}</CardTitle></CardHeader>
            <CardContent className="space-y-2">
              <CodeBox code={current.code} expiresAt={current.expiresAt} />
              <Select value={build} onChange={(e) => setBuild(e.target.value)} className="h-11 w-full">
                {builds.map((b) => <option key={b.build} value={b.build}>{boardTitle(b.board)} · {b.chipFamily}</option>)}
              </Select>
            </CardContent>
          </Card>
          <FlashStep type={type} builds={builds} selected={selectedBuild?.build} onSelect={setBuild} />
          <ConfigureStep code={current.code} deviceId={current.deviceId} />
        </>
      )}
    </div>
  )
}

function OneHornet() {
  const [type, setType] = useState('guard-cam')
  const [deviceId, setDeviceId] = useState('')
  const [name, setName] = useState('')
  const [code, setCode] = useState<{ code: string; expiresAt: string; replaces: boolean } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const firmware = useQuery({ queryKey: ['firmware'], queryFn: () => call(api.GET('/api/firmware')) })
  const builds = (firmware.data ?? []).filter((b) => b.role === type)
  const [build, setBuild] = useState('')
  const selectedBuild = builds.find((b) => b.build === build) ?? builds[0]

  async function createCode(e: FormEvent) {
    e.preventDefault()
    setError(null)
    const { data, error: err, response } = await api.POST('/api/provision/codes', { body: { deviceId, type, name } })
    if (!response.ok || !data) {
      setError((err as { errors?: Record<string, string[]> } | undefined)?.errors?.request?.[0] ?? t('errors.generic'))
      return
    }
    setCode({ code: data.code, expiresAt: data.expiresAt, replaces: data.replaces })
  }

  const idValid = idPattern.test(deviceId) && deviceId.length <= 32

  return (
    <div className="space-y-4">
      <Card>
        <CardHeader><CardTitle>1. {t('add.step1')}</CardTitle>{code && <Badge tone="ok">{code.code}</Badge>}</CardHeader>
        <CardContent>
          {code ? (
            <div className="space-y-2 text-sm">
              <p>{deviceId} · {name}{code.replaces && <span className="text-amber-300"> · {t('add.replaces')}</span>}</p>
              <CodeBox code={code.code} expiresAt={code.expiresAt} />
            </div>
          ) : (
            <form onSubmit={createCode} className="space-y-2">
              <Select value={type} onChange={(e) => setType(e.target.value)} className="h-11 w-full">
                {types.map((x) => <option key={x.code} value={x.code}>{x.title}</option>)}
              </Select>
              <Input placeholder={t('add.idPlaceholder')} value={deviceId} onChange={(e) => setDeviceId(e.target.value.toLowerCase())} />
              {deviceId && !idValid && <p className="text-xs text-amber-300">{t('add.idHint')}</p>}
              <Input placeholder={t('add.namePlaceholder')} value={name} onChange={(e) => setName(e.target.value)} />
              {error && <p className="text-sm text-red-400">{error}</p>}
              <Button type="submit" disabled={!idValid || !name.trim()}>{t('add.getCode')}</Button>
            </form>
          )}
        </CardContent>
      </Card>

      {code && (
        <FlashStep
          type={type}
          builds={builds}
          selected={selectedBuild?.build}
          onSelect={setBuild}
        />
      )}
      {code && <ConfigureStep code={code.code} deviceId={deviceId} />}
    </div>
  )
}

function FlashStep({ type, builds, selected, onSelect }: {
  type: string
  builds: { build: string; board: string; chipFamily: string }[]
  selected?: string
  onSelect: (build: string) => void
}) {
  const [ready, setReady] = useState(false)
  const available = builds.length > 0
  useEffect(() => {
    if (!available || !serialSupported()) return
    // ESP Web Tools (a web component, ~100 KB) is only loaded for this step.
    void import('esp-web-tools/dist/web/install-button.js').then(() => setReady(true))
  }, [available])

  return (
    <Card>
      <CardHeader><CardTitle>2. {t('add.step2')}</CardTitle></CardHeader>
      <CardContent className="space-y-2 text-sm">
        <p className="text-neutral-400">{t('add.flashHint')}</p>
        {!serialSupported() && <p className="text-amber-300">{t('add.noSerial')}</p>}
        {!available && <p className="text-amber-300">{t('add.noFirmware').replace('{type}', type)}</p>}
        {available && (
          <Select value={selected} onChange={(e) => onSelect(e.target.value)} className="h-11 w-full">
            {builds.map((b) => <option key={b.build} value={b.build}>{boardTitle(b.board)} · {b.chipFamily}</option>)}
          </Select>
        )}
        {available && ready && selected &&
          createElement('esp-web-install-button', { key: selected, manifest: `/api/firmware/${selected}/manifest.json` },
            createElement(Button, { slot: 'activate' } as never, createElement(Cpu), t('add.flash')))}
        {available && selected && (
          <a
            href={`/api/firmware/${selected}/firmware.bin?download=1`}
            className="inline-flex items-center gap-2 text-amber-400 hover:underline"
          >
            <Download className="size-4" />{t('add.download')}
          </a>
        )}
        {available && <p className="text-xs text-neutral-500">{t('add.downloadHint')}</p>}
        <p className="text-xs text-neutral-500">{t('add.alreadyFlashed')}</p>
      </CardContent>
    </Card>
  )
}

function ConfigureStep({ code, deviceId }: { code: string; deviceId: string }) {
  const [ssid, setSsid] = useState('')
  const [pass, setPass] = useState('')
  const [hive, setHive] = useState(defaultHiveUrl)
  // The hive keeps the Wi-Fi network once (Settings → Wi-Fi for hornets); admins get it filled in here.
  const [saved, setSaved] = useState(false)
  useEffect(() => {
    void api.GET('/api/settings/wifi/reveal').then(({ data }) => {
      if (!data) return
      setSsid(data.ssid)
      setPass(data.password)
      if (data.hiveUrl) setHive(data.hiveUrl)
      setSaved(true)
    })
  }, [])
  const [log, setLog] = useState<string[]>([])
  const [state, setState] = useState<'idle' | 'sending' | 'enrolled' | 'failed'>('idle')
  const serial = useRef<HornetSerial | null>(null)
  const online = useQuery({
    queryKey: ['device', deviceId],
    queryFn: async () => (await api.GET('/api/devices/{deviceId}', { params: { path: { deviceId } } })).data ?? null,
    refetchInterval: state === 'enrolled' ? 2000 : false,
  })
  const isOnline = online.data?.status === 'online' && state === 'enrolled'

  useEffect(() => () => void serial.current?.close(), [])

  const commands = [`set ssid ${ssid}`, `set pass ${pass}`, `enroll ${hive} ${code}`]

  async function send() {
    setState('sending')
    setLog([])
    try {
      serial.current?.close()
      serial.current = await HornetSerial.open((line) => {
        setLog((l) => [...l.slice(-30), line])
        if (line.startsWith('enrolled ')) setState('enrolled')
        if (line.startsWith('enroll failed')) setState('failed')
      })
      await new Promise((r) => setTimeout(r, 2500)) // the board may have just rebooted
      for (const c of commands) {
        await serial.current.send(c)
        await new Promise((r) => setTimeout(r, 300))
      }
    } catch (e) {
      setState('failed')
      setLog((l) => [...l, String(e)])
    }
  }

  return (
    <Card>
      <CardHeader><CardTitle>3. {t('add.step3')}</CardTitle>{isOnline && <Badge tone="ok"><CheckCircle2 className="size-3" />{t('status.online')}</Badge>}</CardHeader>
      <CardContent className="space-y-2 text-sm">
        {saved && <p className="text-xs text-emerald-400">{t('add.wifiFromHive')}</p>}
        <Input placeholder={t('add.ssid')} value={ssid} onChange={(e) => setSsid(e.target.value)} />
        <PasswordInput placeholder={t('add.pass')} value={pass} onChange={(e) => setPass(e.target.value)} />
        <Input placeholder="http://192.168.1.10:5080" value={hive} onChange={(e) => setHive(e.target.value)} />
        <p className="text-xs text-neutral-500">{t('add.hiveHint')}</p>
        {serialSupported() && (
          <Button onClick={() => void send()} disabled={!ssid || !hive || state === 'sending'}><Usb />{t('add.send')}</Button>
        )}
        {log.length > 0 && (
          <pre className="max-h-40 overflow-auto rounded bg-neutral-950 p-2 text-xs text-neutral-400">{log.join('\n')}</pre>
        )}
        {state === 'enrolled' && !isOnline && <p className="text-amber-300">{t('add.waitingOnline')}</p>}
        {isOnline && <p className="text-emerald-400">{t('add.done')}</p>}
        {state === 'failed' && <p className="text-red-400">{t('add.failed')}</p>}
        <details className="text-xs text-neutral-500">
          <summary className="cursor-pointer">{t('add.manual')}</summary>
          <pre className="mt-1 whitespace-pre-wrap rounded bg-neutral-950 p-2">{commands.join('\n')}</pre>
        </details>
      </CardContent>
    </Card>
  )
}
