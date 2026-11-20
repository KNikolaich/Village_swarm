import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Camera, Maximize2, Minimize2, Pin, Play, Square, Video, WifiOff } from 'lucide-react'
import { useCallback, useEffect, useRef, useState } from 'react'
import { api, call, type EventDto } from '@/api/client'
import { useLatestPhoto, useSnapshot } from '@/api/queries'
import { CarControls } from '@/components/CarControls'
import { PtzControls } from '@/components/PtzControls'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle, Select } from '@/components/ui/primitives'
import { t } from '@/i18n'
import {
  alarmCamera, codecList, codecName, groupCameras, hasVideo, idleAfterMs, idleApplies, isCodecMismatch, isFull, isStalled, kbps, linkedCamera, liveStreams, loadKeepOn, rotatedBox,
  saveKeepOn, stallAfterMs, loadChoice, qualities, saveChoice, shouldPlay,
  type Quality, type VideoCamera, type VideoChoice, type VideoViewers,
} from '@/lib/video'

/**
 * «Видео» on the overview (contracts/ipcam.md "Live video"): one camera of the family's choice in this browser, as frames
 * every 5 s or live through go2rtc (light or detailed stream). Nothing is opened until someone presses play; the stream
 * stops when the tab is hidden, the widget scrolls away or nobody has touched it for 5 minutes («Не выключать» lifts the
 * last while few people watch). An alarm offers its camera but never starts it.
 */
export function VideoWidget({ events }: { events: readonly EventDto[] }) {
  const streams = useQuery({ queryKey: ['video', 'streams'], queryFn: () => call(api.GET('/api/video/streams')), staleTime: 60_000 })
  const cameras = streams.data?.cameras ?? []
  // A link to a camera (?video=car-cam2, the car's «Ездить») picks it and plays at once.
  const linked = linkedCamera(window.location.search)
  const [choice, setChoice] = useState<VideoChoice>(() => linked ? { camera: linked, quality: 'sd' } : loadChoice())
  const [wanted, setWanted] = useState(linked !== null)
  const box = useRef<HTMLDivElement>(null)
  const [visible, setVisible] = useState(() => document.visibilityState === 'visible')
  const [inView, setInView] = useState(true)
  const [idle, setIdle] = useState(false)
  const [lost, setLost] = useState(false)
  const [full, setFull] = useState(false)
  const [keepOn, setKeepOn] = useState(loadKeepOn)
  // The camera and quality whose codec this browser lacked: watched through the hive's H.264 copy from then on.
  const [h264For, setH264For] = useState<string | null>(null)
  const lastTouch = useRef(Date.now())
  const viewers = useQuery({
    queryKey: ['video', 'viewers'],
    queryFn: () => call(api.GET('/api/video/viewers')),
    enabled: streams.data?.enabled === true && wanted,
    refetchInterval: 15_000,
  })

  const camera = cameras.find((c) => c.deviceId === choice.camera) ?? null
  // A guard-cam has no stream, only frames: whatever quality was picked, it is shown as «Снимки».
  const framesOnly = camera?.kind === 'guard-cam'
  const quality: Quality = framesOnly ? 'snapshots' : choice.quality
  const choose = (next: VideoChoice) => {
    setChoice(next)
    saveChoice(next)
    if (!next.camera) setWanted(false)
    setLost(false)
  }
  const play = () => {
    touch()
    setLost(false)
    setWanted(true)
  }
  const touch = useCallback(() => {
    lastTouch.current = Date.now()
    setIdle(false)
  }, [])

  useEffect(() => {
    const onVisibility = () => setVisible(document.visibilityState === 'visible')
    document.addEventListener('visibilitychange', onVisibility)
    return () => document.removeEventListener('visibilitychange', onVisibility)
  }, [])
  useEffect(() => {
    if (!box.current || typeof IntersectionObserver === 'undefined') return
    const observer = new IntersectionObserver(([entry]) => setInView(entry.isIntersecting), { threshold: 0.2 })
    observer.observe(box.current)
    return () => observer.disconnect()
  }, [])
  const idleOn = idleApplies(keepOn, viewers.data)
  useEffect(() => {
    if (!wanted || !idleOn) return
    const timer = setInterval(() => { if (Date.now() - lastTouch.current > idleAfterMs) setIdle(true) }, 10_000)
    return () => clearInterval(timer)
  }, [wanted, idleOn])

  useEffect(() => {
    const onChange = () => setFull(document.fullscreenElement !== null && document.fullscreenElement === box.current)
    document.addEventListener('fullscreenchange', onChange)
    return () => document.removeEventListener('fullscreenchange', onChange)
  }, [])

  const playing = camera !== null && shouldPlay({ wanted, visible, inView, idle })
  const alarm = alarmCamera(events, cameras)

  // Whatever stopped the picture (stop, lost connection, idle, hidden tab) also leaves the whole screen.
  useEffect(() => {
    if (!playing && document.fullscreenElement === box.current) void document.exitFullscreen?.().catch(() => {})
  }, [playing])

  function fullscreen() {
    touch()
    if (full) {
      void document.exitFullscreen?.().catch(() => {})
      return
    }
    void box.current?.requestFullscreen?.().catch(() => {})
    if (!framesOnly && choice.quality === 'sd') choose({ ...choice, quality: 'hd' }) // the detailed stream is worth it on the whole screen
  }

  const lose = useCallback(() => {
    setWanted(false)
    setLost(true)
  }, [])
  const refetchViewers = viewers.refetch
  const refused = useCallback(() => void refetchViewers(), [refetchViewers])
  const live = quality === 'snapshots' || !camera || camera.kind === 'car' ? null : liveStreams(camera, quality)
  const liveKey = camera ? `${camera.deviceId}-${quality}` : ''
  const onH264 = live?.fallback != null && h264For === liveKey

  if (streams.data && !streams.data.enabled) return null

  return (
    <Card className="h-full" onPointerDown={touch} onKeyDown={touch}>
      <CardHeader>
        <CardTitle className="flex items-center gap-2"><Video className="size-4" />{t('video.title')}</CardTitle>
        <div className="flex flex-wrap items-center gap-2">
          <Select className="h-8 max-w-48 text-xs" value={choice.camera ?? ''} aria-label={t('video.camera')}
            onChange={(e) => choose({ ...choice, camera: e.target.value || null })}>
            <option value="">{t('video.noCamera')}</option>
            {groupCameras(cameras, t('video.noZone')).map((g) => (
              <optgroup key={g.zone} label={g.zone}>
                {g.cameras.map((c) => <option key={c.deviceId} value={c.deviceId}>{c.name}</option>)}
              </optgroup>
            ))}
          </Select>
          <button type="button" aria-pressed={keepOn} title={t('video.keepOnHint').replace('{n}', String(viewers.data?.keepOnUpTo ?? 3))}
            onClick={() => { const next = !keepOn; setKeepOn(next); saveKeepOn(next); touch() }}
            className={keepOn ? 'flex items-center gap-1 rounded-md bg-neutral-700 px-2 py-1 text-xs text-neutral-100' : 'flex items-center gap-1 rounded-md px-2 py-1 text-xs text-neutral-400 hover:text-neutral-200'}>
            <Pin className="size-3.5" />{t('video.keepOn')}
          </button>
          <div className="flex rounded-md bg-neutral-900 p-0.5 text-xs" role="radiogroup" aria-label={t('video.quality')}>
            {qualities.map((q) => (
              <button key={q} type="button" role="radio" aria-checked={quality === q} disabled={framesOnly && q !== 'snapshots'} onClick={() => choose({ ...choice, quality: q })}
                className={quality === q ? 'rounded bg-neutral-700 px-2 py-1 text-neutral-100' : 'px-2 py-1 text-neutral-400 hover:text-neutral-200'}>
                {t(`video.qualities.${q}`)}
              </button>
            ))}
          </div>
        </div>
      </CardHeader>
      <CardContent className="space-y-2">
        {alarm && alarm.deviceId !== choice.camera && (
          <Button variant="outline" size="sm" className="border-red-900/70 text-red-300"
            onClick={() => { choose({ camera: alarm.deviceId, quality: choice.quality === 'snapshots' ? 'sd' : choice.quality }); play() }}>
            {t('video.showAlarm').replace('{camera}', alarm.name)}
          </Button>
        )}
        <div ref={box} className="relative aspect-video w-full overflow-hidden rounded-md bg-neutral-950">
          {!camera && <p className="flex h-full items-center justify-center text-sm text-neutral-500">{t('video.pick')}</p>}
          {camera && playing && (
            <Turned degrees={camera.rotate ?? 0} box={box}>
            {quality === 'snapshots'
            ? (framesOnly ? <GuardFrames camera={camera} /> : <Snapshots camera={camera} />)
            : camera.kind === 'car'
              ? <MjpegPlayer key={camera.deviceId} stream={camera.sub} onLost={lose} />
              : live && <LivePlayer key={`${liveKey}-${onH264 ? 'h264' : 'own'}`} stream={onH264 ? live.fallback! : live.stream} quality={quality}
                  transcoded={onH264} viewers={viewers.data} onLost={lose} onRefused={refused}
                  onMismatch={live.fallback && !onH264 ? () => setH264For(liveKey) : undefined} />}
            </Turned>
          )}
          {camera && !playing && (
            <div className="absolute inset-0 flex flex-col items-center justify-center gap-2 text-sm text-neutral-400">
              {idle && <p>{t('video.stillWatching')}</p>}
              {idle && keepOn && viewers.data && <p className="max-w-xs text-center text-xs">{t('video.keepOnBusy').replace('{n}', String(viewers.data.viewers))}</p>}
              {lost && <p className="flex items-center gap-1.5 text-red-300"><WifiOff className="size-4" />{t('video.lost')}</p>}
              <Button onClick={play}><Play />{lost ? t('video.playAgain') : idle ? t('video.continue') : t('video.play')}</Button>
            </div>
          )}
          {camera && playing && (camera.kind === 'car'
            ? <CarControls deviceId={camera.deviceId} onTouch={touch} />
            : <PtzControls deviceId={camera.deviceId} onTouch={touch} />)}
          {camera && playing && (
            <div className="absolute right-2 top-2 flex gap-1">
              {camera.kind !== 'car' && <SnapshotButton deviceId={camera.deviceId} />}
              <Button variant="secondary" size="icon" className="size-8 opacity-80" onClick={fullscreen}
                title={full ? t('video.exitFullscreen') : t('video.fullscreen')} aria-label={full ? t('video.exitFullscreen') : t('video.fullscreen')}>
                {full ? <Minimize2 /> : <Maximize2 />}
              </Button>
              <Button variant="secondary" size="icon" className="size-8 opacity-80" onClick={() => setWanted(false)} title={t('video.stop')} aria-label={t('video.stop')}><Square /></Button>
            </div>
          )}
        </div>
      </CardContent>
    </Card>
  )
}

/** "Take a photo" from the viewer: the camera's own snapshot, saved like any other photo. */
function SnapshotButton({ deviceId }: { deviceId: string }) {
  const takeSnapshot = useSnapshot(deviceId)
  const [busy, setBusy] = useState(false)
  return (
    <Button variant="secondary" size="icon" className="size-8 opacity-80" disabled={busy} title={t('device.snapshot')} aria-label={t('device.snapshot')}
      onClick={() => { setBusy(true); void takeSnapshot().finally(() => setBusy(false)) }}>
      <Camera />
    </Button>
  )
}

/** A frame every 5 s the way photos are taken (the NVR's snapshot, else the stream), while the widget plays. */
function Snapshots({ camera }: { camera: VideoCamera }) {
  const [url, setUrl] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    let stop = false
    let current: string | null = null
    async function next() {
      try {
        // A car has no snapshot of its own: a frame of its stream through go2rtc.
        const url = camera.kind === 'car' ? `/streams/frame.jpeg?src=${encodeURIComponent(camera.sub)}` : `/api/ipcams/${camera.nvrId}/snapshot?channel=${camera.channel}`
        const response = await fetch(url, { credentials: 'same-origin' })
        if (!response.ok) throw new Error(((await response.json().catch(() => null)) as { detail?: string } | null)?.detail ?? `HTTP ${response.status}`)
        const blob = URL.createObjectURL(await response.blob())
        if (stop) return URL.revokeObjectURL(blob)
        if (current) URL.revokeObjectURL(current)
        current = blob
        setUrl(blob)
        setError(null)
      } catch (e) {
        if (!stop) setError(String(e instanceof Error ? e.message : e))
      }
      if (!stop) timer = setTimeout(() => void next(), 5000)
    }
    let timer = setTimeout(() => void next(), 0)
    return () => {
      stop = true
      clearTimeout(timer)
      if (current) URL.revokeObjectURL(current)
    }
  }, [camera.nvrId, camera.channel, camera.kind, camera.sub])
  return (
    <>
      {url && <img src={url} alt={camera.name} className="h-full w-full object-contain" />}
      {error && <p className="absolute inset-x-2 bottom-2 rounded bg-black/70 p-1 text-xs text-red-300">{t('video.snapshotFailed')}: {error}</p>}
    </>
  )
}

/**
 * A guard-cam's frames: it has no stream, so while the widget plays it is asked for a photo (the snapshot command) every
 * 10 s, and the newest photo of the hive is shown, as it arrives. Nothing is asked while the widget does not play.
 */
function GuardFrames({ camera }: { camera: VideoCamera }) {
  const qc = useQueryClient()
  const latest = useLatestPhoto(camera.deviceId)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    let stop = false
    let timer: ReturnType<typeof setTimeout>
    async function next() {
      try {
        await call(api.POST('/api/devices/{deviceId}/commands', {
          params: { path: { deviceId: camera.deviceId } },
          body: { name: 'snapshot', payload: { count: 1 } as unknown as Record<string, never> },
        }))
        if (!stop) setError(null)
        // The photo comes over HTTP a few seconds later.
        for (const ms of [2500, 6000]) setTimeout(() => void qc.invalidateQueries({ queryKey: ['media', 'latest', camera.deviceId] }), ms)
      } catch (e) {
        if (!stop) setError(String(e instanceof Error ? e.message : e))
      }
      if (!stop) timer = setTimeout(() => void next(), 10_000)
    }
    timer = setTimeout(() => void next(), 0)
    return () => {
      stop = true
      clearTimeout(timer)
    }
  }, [camera.deviceId, qc])
  const photo = latest.data
  return (
    <>
      {photo && <img src={photo.url} alt={camera.name} className="h-full w-full object-contain" />}
      {error && <p className="absolute inset-x-2 bottom-2 rounded bg-black/70 p-1 text-xs text-red-300">{t('video.snapshotFailed')}: {error}</p>}
    </>
  )
}

/** The picture of a camera mounted turned (config camera.rotate), turned back in its box; the overlays are outside it. */
function Turned({ degrees, box, children }: { degrees: number; box: React.RefObject<HTMLDivElement | null>; children: React.ReactNode }) {
  const [size, setSize] = useState<{ w: number; h: number } | null>(null)
  useEffect(() => {
    const el = box.current
    if (!el || degrees === 0 || typeof ResizeObserver === 'undefined') return
    const observer = new ResizeObserver(([entry]) => setSize({ w: entry.contentRect.width, h: entry.contentRect.height }))
    observer.observe(el)
    return () => observer.disconnect()
  }, [box, degrees])
  if (degrees === 0 || !size) return <>{children}</>
  const r = rotatedBox(degrees, size.w, size.h)
  return (
    <div className="absolute left-1/2 top-1/2" style={{ width: r.width, height: r.height, transform: r.transform }}>
      {children}
    </div>
  )
}

/**
 * A car's camera: MJPEG, which MSE cannot play, shown as go2rtc's multipart stream in an image. A stream that never
 * starts or breaks off is a lost connection, like a stalled live player.
 */
function MjpegPlayer({ stream, onLost }: { stream: string; onLost: () => void }) {
  const [started, setStarted] = useState(false)
  useEffect(() => {
    if (started) return
    const timer = setTimeout(onLost, stallAfterMs)
    return () => clearTimeout(timer)
  }, [started, onLost])
  return (
    <>
      <img src={`/streams/stream.mjpeg?src=${encodeURIComponent(stream)}`} alt="" className="h-full w-full object-contain"
        onLoad={() => setStarted(true)} onError={onLost} />
      <p className="absolute bottom-2 left-2 rounded bg-black/60 px-1.5 py-0.5 text-[11px] text-neutral-300">MJPEG</p>
    </>
  )
}

/**
 * go2rtc's MSE over WebSocket (its video-rtc protocol): the browser says which codecs it plays, go2rtc answers with the
 * stream's MIME type and then sends fragmented MP4. A camera whose codec the browser lacks (H.265 on most desktops)
 * gets a plain explanation instead of a black box.
 */
function LivePlayer({ stream, quality, transcoded, viewers, onLost, onRefused, onMismatch }: {
  stream: string
  quality: Quality
  /** The hive's H.264 copy of an H.265 camera. */
  transcoded: boolean
  viewers: VideoViewers | undefined
  onLost: () => void
  /** The connection did not open: the hive may have refused one viewer too many; the count is asked again. */
  onRefused: () => void
  /** The browser lacks the camera's codec and there is an H.264 copy to switch to; without it the reason is shown. */
  onMismatch?: () => void
}) {
  const video = useRef<HTMLVideoElement>(null)
  const [state, setState] = useState<{ codec?: string; kbps?: number; error?: string; mismatch?: boolean; refused?: boolean }>({})
  // Kept in refs so a new callback from the parent does not reconnect the stream.
  const mismatchRef = useRef(onMismatch)
  const refusedRef = useRef(onRefused)
  useEffect(() => {
    mismatchRef.current = onMismatch
    refusedRef.current = onRefused
  })

  useEffect(() => {
    const element = video.current
    const Source = (window as { ManagedMediaSource?: typeof MediaSource }).ManagedMediaSource ?? window.MediaSource
    if (!element || !Source) {
      setState({ error: t('video.noMse') })
      return
    }
    const proto = window.location.protocol === 'https:' ? 'wss' : 'ws'
    const ws = new WebSocket(`${proto}://${window.location.host}/streams/ws?src=${encodeURIComponent(stream)}`)
    ws.binaryType = 'arraybuffer'
    let buffer: SourceBuffer | null = null
    let source: MediaSource | null = null
    const queue: ArrayBuffer[] = []
    let bytes = 0
    let since = performance.now()
    let lastData: number | null = null
    let codecProblem = false
    // A stream that started and then stopped coming (camera gone, Wi-Fi dropped, go2rtc stuck) is stopped here and said so.
    const meter = setInterval(() => {
      const now = performance.now()
      setState((s) => ({ ...s, kbps: kbps(bytes, now - since) }))
      bytes = 0
      since = now
      if (isStalled(lastData, now)) onLost()
    }, 2000)

    const pump = () => {
      if (!buffer || buffer.updating || queue.length === 0) return
      try {
        buffer.appendBuffer(queue.shift()!)
      } catch {
        queue.length = 0 // a full buffer: drop what waits, the next fragments catch up
      }
      // Stay live: keep a few seconds, jump ahead when the picture falls behind.
      const ranges = buffer.buffered
      if (ranges.length > 0) {
        const end = ranges.end(ranges.length - 1)
        if (end - element.currentTime > 3) element.currentTime = end - 0.5
        if (!buffer.updating && element.currentTime - ranges.start(0) > 20) {
          try { buffer.remove(ranges.start(0), element.currentTime - 10) } catch { /* updating: next time */ }
        }
      }
    }

    ws.onopen = () => ws.send(JSON.stringify({ type: 'mse', value: codecList((type) => Source.isTypeSupported(type)) }))
    ws.onmessage = (ev) => {
      if (typeof ev.data === 'string') {
        const msg = JSON.parse(ev.data) as { type: string; value: string }
        if (msg.type === 'mse') {
          if (!hasVideo(msg.value)) {
            // No picture among the codecs: the H.264 copy, or said plainly instead of a black box.
            codecProblem = true
            ws.close(1000)
            if (mismatchRef.current) mismatchRef.current()
            else setState((s) => ({ ...s, error: t('video.codecUnsupported'), mismatch: true }))
            return
          }
          setState((s) => ({ ...s, codec: codecName(msg.value) }))
          source = new Source()
          element.src = URL.createObjectURL(source)
          source.addEventListener('sourceopen', () => {
            try {
              buffer = source!.addSourceBuffer(msg.value)
              buffer.mode = 'segments'
              buffer.addEventListener('updateend', pump)
              pump()
            } catch {
              codecProblem = true
              if (mismatchRef.current) mismatchRef.current()
              else setState((s) => ({ ...s, error: t('video.codecUnsupported'), mismatch: true }))
            }
          }, { once: true })
          void element.play().catch(() => {})
        } else if (msg.type === 'error') {
          codecProblem = true // go2rtc said why (a codec, an unreachable camera): shown as it is, not as a lost connection
          if (isCodecMismatch(msg.value) && mismatchRef.current) mismatchRef.current()
          else setState((s) => ({ ...s, error: msg.value, mismatch: isCodecMismatch(msg.value) }))
        }
        return
      }
      bytes += (ev.data as ArrayBuffer).byteLength
      lastData = performance.now()
      queue.push(ev.data as ArrayBuffer)
      pump()
    }
    const failed = () => {
      setState((s) => ({ ...s, error: s.error ?? t('video.connectionFailed'), refused: s.error === undefined }))
      refusedRef.current()
    }
    ws.onerror = () => {
      if (lastData === null) failed()
    }
    ws.onclose = (ev) => {
      // Dropped after the picture came: the connection is lost. Before it, the reason stays on the picture.
      if (ev.code !== 1000 && lastData !== null && !codecProblem) onLost()
      else if (ev.code !== 1000) failed()
    }

    return () => {
      clearInterval(meter)
      ws.onclose = null
      ws.close()
      if (element.src) {
        URL.revokeObjectURL(element.src)
        element.removeAttribute('src')
        element.load()
      }
    }
  }, [stream, onLost])

  return (
    <>
      <video ref={video} muted playsInline autoPlay className="h-full w-full object-contain" />
      <p className="absolute bottom-2 left-2 rounded bg-black/60 px-1.5 py-0.5 text-[11px] text-neutral-300">
        {t(`video.qualities.${quality}`)}{state.codec ? ` · ${state.codec}` : ''}{transcoded ? ` (${t('video.transcoded')})` : ''}
        {state.kbps ? ` · ${state.kbps} kbit/s` : ''}{viewers && viewers.viewers > 1 ? ` · ${t('video.viewers').replace('{n}', String(viewers.viewers))}` : ''}
      </p>
      {state.error && (
        <div className="absolute inset-x-2 top-2 space-y-1 rounded bg-black/80 p-2 text-xs text-red-300">
          <p>{state.mismatch ? t('video.codecUnsupported') : state.refused && isFull(viewers) ? t('video.tooMany').replace('{n}', String(viewers!.viewers)) : state.error}</p>
          {state.mismatch && <p className="text-neutral-300">{t('video.codecHint')}</p>}
        </div>
      )}
    </>
  )
}
