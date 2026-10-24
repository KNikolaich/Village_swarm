import { useQuery } from '@tanstack/react-query'
import { Maximize2, Play, Square, Video } from 'lucide-react'
import { useCallback, useEffect, useRef, useState } from 'react'
import { api, call, type EventDto } from '@/api/client'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle, Select } from '@/components/ui/primitives'
import { t } from '@/i18n'
import {
  alarmCamera, codecList, codecName, groupCameras, idleAfterMs, isCodecMismatch, kbps, loadChoice, qualities, saveChoice, shouldPlay,
  type Quality, type VideoCamera, type VideoChoice,
} from '@/lib/video'

/**
 * «Видео» on the overview (contracts/ipcam.md "Live video"): one camera of the family's choice in this browser, as frames
 * every 5 s or live through go2rtc (light or detailed stream). Nothing is opened until someone presses play; the stream
 * stops when the tab is hidden, the widget scrolls away or nobody has touched it for 5 minutes. An alarm offers its camera
 * but never starts it.
 */
export function VideoWidget({ events }: { events: readonly EventDto[] }) {
  const streams = useQuery({ queryKey: ['video', 'streams'], queryFn: () => call(api.GET('/api/video/streams')), staleTime: 60_000 })
  const cameras = streams.data?.cameras ?? []
  const [choice, setChoice] = useState<VideoChoice>(() => loadChoice())
  const [wanted, setWanted] = useState(false)
  const box = useRef<HTMLDivElement>(null)
  const [visible, setVisible] = useState(() => document.visibilityState === 'visible')
  const [inView, setInView] = useState(true)
  const [idle, setIdle] = useState(false)
  const lastTouch = useRef(Date.now())

  const camera = cameras.find((c) => c.deviceId === choice.camera) ?? null
  const choose = (next: VideoChoice) => {
    setChoice(next)
    saveChoice(next)
    if (!next.camera) setWanted(false)
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
  useEffect(() => {
    if (!wanted) return
    const timer = setInterval(() => { if (Date.now() - lastTouch.current > idleAfterMs) setIdle(true) }, 10_000)
    return () => clearInterval(timer)
  }, [wanted])

  const playing = camera !== null && shouldPlay({ wanted, visible, inView, idle })
  const alarm = alarmCamera(events, cameras)

  function fullscreen() {
    touch()
    void box.current?.requestFullscreen?.().catch(() => {})
    if (choice.quality === 'sd') choose({ ...choice, quality: 'hd' }) // the detailed stream is worth it on the whole screen
  }

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
          <div className="flex rounded-md bg-neutral-900 p-0.5 text-xs" role="radiogroup" aria-label={t('video.quality')}>
            {qualities.map((q) => (
              <button key={q} type="button" role="radio" aria-checked={choice.quality === q} onClick={() => choose({ ...choice, quality: q })}
                className={choice.quality === q ? 'rounded bg-neutral-700 px-2 py-1 text-neutral-100' : 'px-2 py-1 text-neutral-400 hover:text-neutral-200'}>
                {t(`video.qualities.${q}`)}
              </button>
            ))}
          </div>
        </div>
      </CardHeader>
      <CardContent className="space-y-2">
        {alarm && alarm.deviceId !== choice.camera && (
          <Button variant="outline" size="sm" className="border-red-900/70 text-red-300"
            onClick={() => { choose({ camera: alarm.deviceId, quality: choice.quality === 'snapshots' ? 'sd' : choice.quality }); touch(); setWanted(true) }}>
            {t('video.showAlarm').replace('{camera}', alarm.name)}
          </Button>
        )}
        <div ref={box} className="relative aspect-video w-full overflow-hidden rounded-md bg-neutral-950">
          {!camera && <p className="flex h-full items-center justify-center text-sm text-neutral-500">{t('video.pick')}</p>}
          {camera && playing && (choice.quality === 'snapshots'
            ? <Snapshots camera={camera} />
            : <LivePlayer key={`${camera.deviceId}-${choice.quality}`} stream={choice.quality === 'hd' ? camera.main : camera.sub} quality={choice.quality} />)}
          {camera && !playing && (
            <div className="absolute inset-0 flex flex-col items-center justify-center gap-2 text-sm text-neutral-400">
              {idle && <p>{t('video.stillWatching')}</p>}
              <Button onClick={() => { touch(); setWanted(true) }}><Play />{idle ? t('video.continue') : t('video.play')}</Button>
            </div>
          )}
          {camera && playing && (
            <div className="absolute right-2 top-2 flex gap-1">
              <Button variant="secondary" size="icon" className="size-8 opacity-80" onClick={fullscreen} title={t('video.fullscreen')} aria-label={t('video.fullscreen')}><Maximize2 /></Button>
              <Button variant="secondary" size="icon" className="size-8 opacity-80" onClick={() => setWanted(false)} title={t('video.stop')} aria-label={t('video.stop')}><Square /></Button>
            </div>
          )}
        </div>
      </CardContent>
    </Card>
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
        const response = await fetch(`/api/ipcams/${camera.nvrId}/snapshot?channel=${camera.channel}`, { credentials: 'same-origin' })
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
  }, [camera.nvrId, camera.channel])
  return (
    <>
      {url && <img src={url} alt={camera.name} className="h-full w-full object-contain" />}
      {error && <p className="absolute inset-x-2 bottom-2 rounded bg-black/70 p-1 text-xs text-red-300">{t('video.snapshotFailed')}: {error}</p>}
    </>
  )
}

/**
 * go2rtc's MSE over WebSocket (its video-rtc protocol): the browser says which codecs it plays, go2rtc answers with the
 * stream's MIME type and then sends fragmented MP4. A camera whose codec the browser lacks (H.265 on most desktops)
 * gets a plain explanation instead of a black box.
 */
function LivePlayer({ stream, quality }: { stream: string; quality: Quality }) {
  const video = useRef<HTMLVideoElement>(null)
  const [state, setState] = useState<{ codec?: string; kbps?: number; error?: string; mismatch?: boolean }>({})

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
    const meter = setInterval(() => {
      const now = performance.now()
      setState((s) => ({ ...s, kbps: kbps(bytes, now - since) }))
      bytes = 0
      since = now
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
              setState((s) => ({ ...s, error: t('video.codecUnsupported'), mismatch: true }))
            }
          }, { once: true })
          void element.play().catch(() => {})
        } else if (msg.type === 'error') {
          setState((s) => ({ ...s, error: msg.value, mismatch: isCodecMismatch(msg.value) }))
        }
        return
      }
      bytes += (ev.data as ArrayBuffer).byteLength
      queue.push(ev.data as ArrayBuffer)
      pump()
    }
    ws.onerror = () => setState((s) => ({ ...s, error: s.error ?? t('video.connectionFailed') }))
    ws.onclose = (ev) => {
      if (ev.code !== 1000) setState((s) => ({ ...s, error: s.error ?? t('video.connectionClosed') }))
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
  }, [stream])

  return (
    <>
      <video ref={video} muted playsInline autoPlay className="h-full w-full object-contain" />
      <p className="absolute bottom-2 left-2 rounded bg-black/60 px-1.5 py-0.5 text-[11px] text-neutral-300">
        {t(`video.qualities.${quality}`)}{state.codec ? ` · ${state.codec}` : ''}{state.kbps ? ` · ${state.kbps} kbit/s` : ''}
      </p>
      {state.error && (
        <div className="absolute inset-x-2 top-2 space-y-1 rounded bg-black/80 p-2 text-xs text-red-300">
          <p>{state.mismatch ? t('video.codecUnsupported') : state.error}</p>
          {state.mismatch && <p className="text-neutral-300">{t('video.codecHint')}</p>}
        </div>
      )}
    </>
  )
}
