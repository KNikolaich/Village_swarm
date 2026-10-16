import { ArrowLeft, Eraser, PlugZap, RotateCcw, Send, Unplug } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router'
import { toast } from 'sonner'
import { api, call } from '@/api/client'
import { useDevices } from '@/api/queries'
import { Button } from '@/components/ui/button'
import { Badge, Card, CardContent, CardHeader, CardTitle, Input } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { HornetSerial, serialSupported } from '@/lib/serial'

type Line = { id: number; kind: 'in' | 'out' | 'info'; text: string; count: number }

const MAX_LINES = 1000

/** The hive host out of an address like http://192.168.10.4 (what a hornet needs as its MQTT server). */
const hostOf = (url: string) => {
  try {
    return new URL(url).hostname
  } catch {
    return url.replace(/^https?:\/\//, '').split(/[/:]/)[0]
  }
}

/** A line as shown: secrets typed into the console are not kept on screen. */
const masked = (line: string) => line.replace(/^(set\s+(?:pass|mqttpass|token)\s+).*/i, '$1***')

/**
 * The rescue console: talks to a hornet over USB (Web Serial) in the hive's own page, so a board that has lost its
 * network can be looked at and fixed without any other tool. The same commands the firmware's serial console takes.
 */
export function ConsolePage() {
  const [lines, setLines] = useState<Line[]>([])
  const [connected, setConnected] = useState(false)
  const [input, setInput] = useState('')
  const [custom, setCustom] = useState({ ssid: '', pass: '', id: '' })
  const devices = useDevices(true)
  const [join, setJoin] = useState({ deviceId: '', url: '', code: '' })
  const [joining, setJoining] = useState(false)
  const port = useRef<HornetSerial | null>(null)
  const nextId = useRef(1)
  const log = useRef<HTMLDivElement>(null)
  const nudge = useRef<number | null>(null)
  const history = useRef<string[]>([])
  const historyAt = useRef(-1)

  function push(kind: Line['kind'], text: string) {
    setLines((old) => {
      // ESP-IDF log lines carry a changing time, "E (3239) cam_hal: ..."; the same message again is one line with a counter.
      const same = (a: string) => a.replace(/^([EWIDV]) \(\d+\) /, '$1 ') === text.replace(/^([EWIDV]) \(\d+\) /, '$1 ')
      const last = old[old.length - 1]
      if (last && last.kind === kind && same(last.text)) return [...old.slice(0, -1), { ...last, text, count: last.count + 1 }]
      return [...old.slice(-(MAX_LINES - 1)), { id: nextId.current++, kind, text, count: 1 }]
    })
  }

  useEffect(() => {
    log.current?.scrollTo({ top: log.current.scrollHeight })
  }, [lines])

  useEffect(() => () => {
    if (nudge.current) window.clearInterval(nudge.current)
    void port.current?.close()
  }, [])

  async function connect() {
    try {
      port.current = await HornetSerial.open((line) => {
        push('in', line)
        // The board says it restarted because the supply sagged (a Wi-Fi burst on a weak USB adapter): say what to do.
        if (/reset=brownout|Brownout detector/i.test(line)) push('info', t('console.brownout'))
      })
      setConnected(true)
      push('info', t('console.connected'))
      // A sleeping hornet stays awake while someone is typing: a newline twice a second for the first 25 seconds.
      let ticks = 0
      nudge.current = window.setInterval(() => {
        if (++ticks > 50 || !port.current) {
          if (nudge.current) window.clearInterval(nudge.current)
          return
        }
        void port.current.send('').catch(() => undefined)
      }, 500)
    } catch (e) {
      if ((e as DOMException).name !== 'NotFoundError') toast.error(t('console.openFailed'))
    }
  }

  async function disconnect() {
    if (nudge.current) window.clearInterval(nudge.current)
    await port.current?.close()
    port.current = null
    setConnected(false)
    push('info', t('console.disconnected'))
  }

  async function run(command: string) {
    if (!port.current) return
    push('out', `> ${masked(command)}`)
    try {
      await port.current.send(command)
    } catch {
      toast.error(t('console.sendFailed'))
    }
  }

  async function resetBoard() {
    try {
      await port.current?.pulseReset()
      push('info', t('console.resetSent'))
    } catch {
      toast.error(t('console.sendFailed'))
    }
  }

  /** Writes the hive's Wi-Fi (Settings) into the board and reads the settings back. */
  async function rescueWifi(alsoAddress: boolean) {
    const secrets = await call(api.GET('/api/settings/wifi/reveal')).catch(() => null)
    if (!secrets?.ssid) {
      toast.error(t('console.noHiveWifi'))
      return
    }
    if (!window.confirm(t('console.rescueConfirm').replace('{ssid}', secrets.ssid))) return
    await run(`set ssid ${secrets.ssid}`)
    await run(`set pass ${secrets.password}`)
    if (alsoAddress && secrets.hiveUrl) {
      await run(`set mqtt ${hostOf(secrets.hiveUrl)}`)
      await run(`set upload ${secrets.hiveUrl}`)
    }
    await run('show')
    toast.success(t('console.rescueDone'))
  }

  /**
   * Enrolls the board in the hive: Wi-Fi from Settings, a one-time code (issued here for the chosen hornet unless one is
   * typed), then `enroll <hive address> <code>`: the board fetches its id, broker address, MQTT login and photo token itself.
   */
  async function joinHive() {
    setJoining(true)
    try {
      const secrets = await call(api.GET('/api/settings/wifi/reveal')).catch(() => null)
      if (!secrets?.ssid) {
        toast.error(t('console.noHiveWifi'))
        return
      }
      const url = (join.url || secrets.hiveUrl || '').trim()
      if (!url) {
        toast.error(t('console.joinNoUrl'))
        return
      }
      let code = join.code.trim()
      if (!code) {
        if (!join.deviceId) {
          toast.error(t('console.joinNeedChoice'))
          return
        }
        const issued = await call(api.POST('/api/devices/{deviceId}/replace', { params: { path: { deviceId: join.deviceId } } })).catch(() => null)
        if (!issued) {
          toast.error(t('console.joinNoCode'))
          return
        }
        code = issued.code
      }
      if (!window.confirm(t('console.joinConfirm').replace('{url}', url).replace('{ssid}', secrets.ssid))) return
      await run(`set ssid ${secrets.ssid}`)
      await run(`set pass ${secrets.password}`)
      await run(`enroll ${url} ${code}`)
      setJoin({ ...join, code: '' })
      toast.success(t('console.joinSent'))
    } finally {
      setJoining(false)
    }
  }

  function submit() {
    const line = input.trim()
    if (!line) return
    history.current.unshift(line)
    historyAt.current = -1
    setInput('')
    void run(line)
  }

  function recall(delta: number) {
    const next = Math.min(history.current.length - 1, Math.max(-1, historyAt.current + delta))
    historyAt.current = next
    setInput(next < 0 ? '' : history.current[next])
  }

  const commands: { cmd: string; label: string; hint: string; confirm?: string }[] = [
    { cmd: 'show', label: 'show', hint: t('console.cmd.show') },
    { cmd: 'help', label: 'help', hint: t('console.cmd.help') },
    { cmd: 'awake', label: 'awake', hint: t('console.cmd.awake') },
    { cmd: 'reboot', label: 'reboot', hint: t('console.cmd.reboot') },
    { cmd: 'factory', label: 'factory', hint: t('console.cmd.factory'), confirm: t('console.factoryConfirm') },
  ]

  if (!serialSupported())
    return (
      <div className="space-y-3">
        <Link to="/swarm" className="flex items-center gap-1 text-sm text-amber-400"><ArrowLeft className="size-4" />{t('device.back')}</Link>
        <Card><CardContent><p className="py-6 text-center text-sm text-neutral-400">{t('console.unsupported')}</p></CardContent></Card>
      </div>
    )

  return (
    <div className="space-y-4">
      <Link to="/swarm" className="flex items-center gap-1 text-sm text-amber-400"><ArrowLeft className="size-4" />{t('device.back')}</Link>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h1 className="text-lg font-semibold">{t('console.title')}</h1>
        <div className="flex items-center gap-2">
          <Badge tone={connected ? 'ok' : 'neutral'}>{connected ? t('console.statusOn') : t('console.statusOff')}</Badge>
          {connected
            ? <Button variant="outline" onClick={() => void disconnect()}><Unplug />{t('console.disconnect')}</Button>
            : <Button onClick={() => void connect()}><PlugZap />{t('console.connect')}</Button>}
        </div>
      </div>
      <p className="text-xs text-neutral-500">{t('console.hint')}</p>

      <Card>
        <CardHeader>
          <CardTitle>{t('console.output')}</CardTitle>
          <div className="flex items-center gap-1">
            <Button variant="ghost" size="sm" disabled={!connected} onClick={() => void resetBoard()} title={t('console.resetHint')}><RotateCcw />{t('console.reset')}</Button>
            <Button variant="ghost" size="sm" onClick={() => setLines([])}><Eraser />{t('console.clear')}</Button>
          </div>
        </CardHeader>
        <CardContent className="space-y-2">
          <div ref={log} className="h-72 overflow-y-auto rounded-lg bg-black/60 p-3 font-mono text-xs leading-relaxed">
            {lines.length === 0 && <p className="text-neutral-600">{t('console.empty')}</p>}
            {lines.map((l) => (
              <div key={l.id} className={l.kind === 'out' ? 'text-amber-300' : l.kind === 'info' ? 'text-sky-300' : 'text-neutral-300'}>{l.text}{l.count > 1 && <span className="ml-2 rounded bg-neutral-800 px-1 text-neutral-400">×{l.count}</span>}</div>
            ))}
          </div>
          <form onSubmit={(e) => { e.preventDefault(); submit() }} className="flex gap-2">
            <Input
              className="font-mono"
              placeholder={t('console.placeholder')}
              value={input}
              disabled={!connected}
              onChange={(e) => setInput(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'ArrowUp') { e.preventDefault(); recall(1) }
                if (e.key === 'ArrowDown') { e.preventDefault(); recall(-1) }
              }}
            />
            <Button type="submit" disabled={!connected || !input.trim()}><Send />{t('console.send')}</Button>
          </form>
        </CardContent>
      </Card>

      <Card>
        <CardHeader><CardTitle>{t('console.rescue')}</CardTitle></CardHeader>
        <CardContent className="space-y-3 text-sm">
          <p className="text-neutral-400">{t('console.rescueHint')}</p>
          <div className="flex flex-wrap gap-2">
            <Button disabled={!connected} onClick={() => void rescueWifi(false)}>{t('console.rescueWifi')}</Button>
            <Button variant="outline" disabled={!connected} onClick={() => void rescueWifi(true)}>{t('console.rescueAll')}</Button>
            <Button variant="outline" disabled={!connected} onClick={() => void run('reboot')}>{t('console.rebootNow')}</Button>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader><CardTitle>{t('console.join')}</CardTitle></CardHeader>
        <CardContent className="space-y-3 text-sm">
          <p className="text-neutral-400">{t('console.joinHint')}</p>
          <div className="grid gap-2 sm:grid-cols-3">
            <select
              className="h-9 rounded-md border border-neutral-700 bg-neutral-900 px-2 text-sm"
              value={join.deviceId}
              onChange={(e) => setJoin({ ...join, deviceId: e.target.value })}
            >
              <option value="">{t('console.joinPick')}</option>
              {(devices.data ?? []).filter((d) => d.type !== 'hive').map((d) => <option key={d.deviceId} value={d.deviceId}>{d.name} ({d.deviceId})</option>)}
            </select>
            <Input placeholder={t('console.joinUrl')} value={join.url} onChange={(e) => setJoin({ ...join, url: e.target.value })} />
            <Input placeholder={t('console.joinCode')} className="font-mono" value={join.code} onChange={(e) => setJoin({ ...join, code: e.target.value })} />
          </div>
          <Button disabled={!connected || joining || (!join.deviceId && !join.code.trim())} onClick={() => void joinHive()}>{t('console.joinGo')}</Button>
        </CardContent>
      </Card>

      <Card>
        <CardHeader><CardTitle>{t('console.manual')}</CardTitle></CardHeader>
        <CardContent className="space-y-3">
          <div className="grid gap-2 sm:grid-cols-3">
            <Input placeholder={t('console.ssid')} value={custom.ssid} onChange={(e) => setCustom({ ...custom, ssid: e.target.value })} />
            <Input placeholder={t('console.pass')} type="password" value={custom.pass} onChange={(e) => setCustom({ ...custom, pass: e.target.value })} />
            <Input placeholder={t('console.id')} value={custom.id} onChange={(e) => setCustom({ ...custom, id: e.target.value })} />
          </div>
          <div className="flex flex-wrap gap-2">
            <Button variant="outline" disabled={!connected || !custom.ssid} onClick={() => void run(`set ssid ${custom.ssid}`)}>set ssid</Button>
            <Button variant="outline" disabled={!connected || !custom.pass} onClick={() => void run(`set pass ${custom.pass}`)}>set pass</Button>
            <Button variant="outline" disabled={!connected || !custom.id} onClick={() => void run(`set id ${custom.id}`)}>set id</Button>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader><CardTitle>{t('console.commands')}</CardTitle></CardHeader>
        <CardContent>
          <ul className="divide-y divide-neutral-800">
            {commands.map((c) => (
              <li key={c.cmd} className="flex items-center justify-between gap-3 py-2">
                <div className="min-w-0">
                  <p className="font-mono text-sm">{c.label}</p>
                  <p className="text-xs text-neutral-500">{c.hint}</p>
                </div>
                <Button size="sm" variant="outline" disabled={!connected} onClick={() => { if (!c.confirm || window.confirm(c.confirm)) void run(c.cmd) }}>{t('console.run')}</Button>
              </li>
            ))}
            <li className="py-2">
              <p className="font-mono text-sm">enroll ADDRESS CODE</p>
              <p className="text-xs text-neutral-500">{t('console.cmd.enroll')}</p>
            </li>
            <li className="py-2 text-xs text-neutral-500">{t('console.setKeys')}</li>
          </ul>
        </CardContent>
      </Card>
    </div>
  )
}
