import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowRight, Cpu, PartyPopper, RotateCcw, Terminal } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { api, call } from '@/api/client'
import { keys } from '@/api/queries'
import { HelpTip } from '@/components/HelpTip'
import { PhoneSetup } from '@/components/PhoneSetup'
import { SetupStages } from '@/components/SetupStages'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle, Input, Select } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { SetupError, type SetupStage } from '@/lib/hornetSetup'
import { publicHiveUrl, rememberHiveUrl } from '@/lib/publicUrl'
import { remoteStages, setupRemote } from '@/lib/remoteSetup'
import { type SerialPortLike, serialSupported } from '@/lib/serial'

/** Boards whose picture the hive draws (DisplayScreens.RenderedBoards) and the firmware build for each. */
const remoteBoards = [{ board: 'waveshare-s3-epaper-154', build: 'monitor-epd154' }]

const idPattern = /^[a-z][a-z0-9]*(-[a-z0-9]+)*$/
const intervals = [5, 10, 15, 30, 60, 120]

/**
 * A monitor far from the hive (contracts/monitor-http.md) in one go: the monitor is added, given a token, the board on USB
 * is flashed and told the Wi-Fi, the hive's address and the token, and, when it stands at that Wi-Fi, reads its first
 * picture, which proves the whole way works. With `existingId` the monitor is already in the swarm (a flash that failed,
 * a new board): only the token and the board are done again.
 */
export function RemoteMonitorPanel({ existingId }: { existingId?: string }) {
  const qc = useQueryClient()
  const navigate = useNavigate()
  const existing = useQuery({
    queryKey: ['device', existingId],
    queryFn: async () => (await api.GET('/api/devices/{deviceId}', { params: { path: { deviceId: existingId! } } })).data ?? null,
    enabled: !!existingId,
  })
  const [board, setBoard] = useState(remoteBoards[0].board)
  const [deviceId, setDeviceId] = useState('monitor-')
  const [name, setName] = useState('')
  const [hive, setHive] = useState(publicHiveUrl)
  const [ssid, setSsid] = useState('')
  const [pass, setPass] = useState('')
  const [interval, setInterval] = useState(10)
  const [here, setHere] = useState(true)
  const [flashed, setFlashed] = useState(false)
  // The home Wi-Fi from the hive's settings is a start for the first try next to the hive.
  useEffect(() => {
    void api.GET('/api/settings/wifi/reveal').then(({ data }) => {
      if (!data) return
      setSsid((v) => v || data.ssid)
      setPass((v) => v || data.password)
    })
  }, [])
  useEffect(() => {
    if (existing.data?.hw) setBoard(existing.data.hw)
  }, [existing.data])

  const firmware = useQuery({ queryKey: ['firmware'], queryFn: () => call(api.GET('/api/firmware')) })
  const build = (firmware.data ?? []).find((b) => b.build === remoteBoards.find((x) => x.board === board)?.build)
  const id = existingId ?? deviceId
  const idValid = idPattern.test(id) && id.length <= 32

  const [log, setLog] = useState<string[]>([])
  const [stage, setStage] = useState<SetupStage | null>(null)
  const [progress, setProgress] = useState(0)
  const [result, setResult] = useState<'running' | 'done' | SetupError | null>(null)
  const [token, setToken] = useState<string | null>(null)
  const created = useRef(false)
  const stageNow = useRef<SetupStage>('port')
  const onStage = (s: SetupStage) => {
    stageNow.current = s
    setStage(s)
  }

  async function run() {
    setLog([])
    setProgress(0)
    setResult('running')
    onStage('port')
    let port: SerialPortLike
    try {
      // first, while the click is fresh: the browser lets a page ask for a port only then
      port = await (navigator as unknown as { serial: { requestPort(): Promise<SerialPortLike> } }).serial.requestPort()
    } catch {
      setResult(null)  // the picker was closed: nothing happened
      setStage(null)
      return
    }
    try {
      if (!existingId && !created.current) {
        const { error, response } = await api.POST('/api/devices/remote-monitors', { body: { deviceId, name: name.trim(), board } })
        if (!response.ok) {
          const text = (error as { errors?: Record<string, string[]> } | undefined)?.errors?.deviceId?.[0]
          throw new SetupError('port', 'other', text ?? t('errors.generic'))
        }
        created.current = true
        await qc.invalidateQueries({ queryKey: keys.devices })
      }
      const issued = await api.POST('/api/devices/{deviceId}/screen-access', { params: { path: { deviceId: id } } })
      if (!issued.response.ok || !issued.data) throw new SetupError('port', 'other', t('errors.generic'))
      setToken(issued.data.token)
      rememberHiveUrl(hive)
      await setupRemote({
        port,
        build: flashed || !build ? undefined : { build: build.build, chipFamily: build.chipFamily },
        settings: { ssid, pass, url: hive, token: issued.data.token, intervalMin: interval },
        readNow: here,
        onStage,
        onProgress: setProgress,
        onLog: (line) => setLog((l) => [...l.slice(-60), line]),
      })
      await qc.invalidateQueries({ queryKey: keys.devices })
      setResult('done')
    } catch (e) {
      setResult(e instanceof SetupError ? e : new SetupError(stageNow.current, 'other', String(e)))
      setLog((l) => [...l, String(e instanceof Error ? e.message : e)])
    }
  }

  const running = result === 'running'
  const failed = result instanceof SetupError ? result : null
  const stages = remoteStages.filter((s) => !(flashed && s === 'flash'))
  const ready = idValid && (existingId || name.trim() !== '') && ssid !== '' && /^https?:\/\/.+/.test(hive.trim())

  if (result === 'done')
    return (
      <Card>
        <CardContent className="flex flex-col items-center gap-3 py-8 text-center">
          <PartyPopper className="size-14 text-amber-400" />
          <p className="text-base">{t(here ? 'remoteMonitor.doneHere' : 'remoteMonitor.doneAway')}</p>
          <p className="text-xs text-neutral-500">{t(here ? 'remoteMonitor.doneHereHint' : 'remoteMonitor.doneAwayHint').replace('{ssid}', ssid)}</p>
          <Button onClick={() => void navigate(`/swarm/${id}`)}>{t('remoteMonitor.choose')}<ArrowRight /></Button>
        </CardContent>
      </Card>
    )

  return (
    <Card>
      <CardHeader><CardTitle>{existingId ? t('remoteMonitor.reflashTitle') : t('remoteMonitor.title')}</CardTitle></CardHeader>
      <CardContent className="space-y-3 text-sm">
        <p className="flex items-start gap-1 text-xs text-neutral-500">
          <span>{t(existingId ? 'remoteMonitor.reflashHint' : 'remoteMonitor.hint')}</span>
          <HelpTip text={t('remoteMonitor.help')} />
        </p>
        <div className="space-y-2">
          {!existingId && (
            <>
              <label className="block space-y-1">
                <span className="text-xs text-neutral-400">{t('remoteMonitor.board')}</span>
                <Select value={board} disabled={running} onChange={(e) => setBoard(e.target.value)}>
                  {remoteBoards.map((b) => <option key={b.board} value={b.board}>{t(`boards.${b.board}`, b.board)}</option>)}
                </Select>
              </label>
              <label className="block space-y-1">
                <span className="text-xs text-neutral-400">{t('remoteMonitor.id')}</span>
                <Input value={deviceId} disabled={running || created.current} onChange={(e) => setDeviceId(e.target.value.toLowerCase())} aria-invalid={!idValid} />
              </label>
              <label className="block space-y-1">
                <span className="text-xs text-neutral-400">{t('remoteMonitor.name')}</span>
                <Input value={name} disabled={running} placeholder={t('remoteMonitor.namePlaceholder')} onChange={(e) => setName(e.target.value)} />
              </label>
            </>
          )}
          {existingId && <p>{existing.data?.name ?? existingId} · {t(`boards.${board}`, board)}</p>}
          <label className="block space-y-1">
            <span className="flex items-center gap-1 text-xs text-neutral-400">
              {t('remoteMonitor.hive')}
              <HelpTip text={t('remoteMonitor.hiveHelp')} />
            </span>
            <Input value={hive} disabled={running} placeholder="https://home.name.keenetic.pro" onChange={(e) => setHive(e.target.value)} />
          </label>
          <label className="block space-y-1">
            <span className="text-xs text-neutral-400">{t('remoteMonitor.ssid')}</span>
            <Input value={ssid} disabled={running} onChange={(e) => setSsid(e.target.value)} />
          </label>
          <label className="block space-y-1">
            <span className="text-xs text-neutral-400">{t('remoteMonitor.pass')}</span>
            <Input value={pass} disabled={running} autoComplete="off" onChange={(e) => setPass(e.target.value)} />
          </label>
          <label className="block space-y-1">
            <span className="text-xs text-neutral-400">{t('remoteMonitor.interval')}</span>
            <Select value={interval} disabled={running} onChange={(e) => setInterval(Number(e.target.value))}>
              {intervals.map((m) => <option key={m} value={m}>{t('remoteMonitor.every').replace('{n}', String(m))}</option>)}
            </Select>
          </label>
          <label className="flex items-start gap-2 text-xs text-neutral-300">
            <input type="checkbox" className="mt-0.5" checked={here} disabled={running} onChange={(e) => setHere(e.target.checked)} />
            <span>{t('remoteMonitor.here')}</span>
          </label>
          <label className="flex items-center gap-2 text-xs text-neutral-300">
            <input type="checkbox" checked={flashed || !build} disabled={running || !build} onChange={(e) => setFlashed(e.target.checked)} />
            {t('add.alreadyFlashed')}
          </label>
        </div>
        {serialSupported() ? (
          <Button onClick={() => void run()} disabled={!ready || running}>
            <Cpu />{t(flashed || !build ? 'remoteMonitor.send' : existingId ? 'remoteMonitor.flashAgain' : 'remoteMonitor.addAndFlash')}
          </Button>
        ) : (
          <p className="text-amber-300">{t('add.noSerial')}</p>
        )}
        {!build && !firmware.isLoading && <p className="text-xs text-amber-300">{t('remoteMonitor.noBuild').replace('{build}', 'monitor-epd154')}</p>}
        {stage && <SetupStages stages={stages} stage={stage} running={running} failed={failed} progress={progress} names="remoteMonitor.stage" />}
        {failed && (
          <div className="space-y-2 rounded-lg border border-red-900/60 bg-red-950/20 p-2">
            <p className="text-red-300">{t(`remoteMonitor.why.${failed.reason}`, t(`add.why.${failed.reason}`, t('add.why.other')))}</p>
            <div className="flex flex-wrap gap-3">
              <Button variant="secondary" size="sm" onClick={() => void run()} disabled={!ready}><RotateCcw />{t('add.retry')}</Button>
              <Link to="/console" className="inline-flex items-center gap-1 text-sm text-amber-400 hover:underline"><Terminal className="size-4" />{t('add.openConsole')}</Link>
            </div>
          </div>
        )}
        {log.length > 0 && (failed || running) && (
          <details open={!!failed} className="text-xs text-neutral-500">
            <summary className="cursor-pointer">{t('add.boardLog')}</summary>
            <pre className="mt-1 max-h-48 overflow-auto rounded bg-neutral-950 p-2 text-neutral-400">{log.join('\n')}</pre>
          </details>
        )}
        {token && <PhoneSetup url={hive} token={token} ssid={ssid} pass={pass} intervalMin={interval} />}
      </CardContent>
    </Card>
  )
}
