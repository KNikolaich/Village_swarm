import { useQuery } from '@tanstack/react-query'
import { ChevronDown, ChevronLeft, ChevronRight, ChevronUp, Play, Square, ZoomIn, ZoomOut } from 'lucide-react'
import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { api, call } from '@/api/client'
import { useMe } from '@/api/queries'
import { t } from '@/i18n'
import {
  loadTourPause, nextTourIndex, ptzMove, ptzRepeatMs, saveTourPause, tourPauses, tourPresets, tourStepMs,
  type PtzDirection, type PtzRequest,
} from '@/lib/video'

/**
 * Arrows, zoom and presets over the picture of a camera that turns (contracts/ipcam.md "PTZ"), for users who may control.
 * A held button repeats its move every 400 ms and sends stop on release; if the stop is lost the hive stops the camera
 * itself a second after the last move. The presets are the first 16 (cameras report all their slots, set or not), with a
 * tour: preset after preset, the travel and a pause to look, round and round until stopped, an arrow is pressed or the
 * stream closes. A preset that fails is skipped. Nothing is shown for a camera without PTZ.
 */
export function PtzControls({ deviceId, onTouch }: { deviceId: string; onTouch?: () => void }) {
  const me = useMe()
  const canControl = me.data?.roles.some((r) => r === 'admin' || r === 'member') ?? false
  const state = useQuery({
    queryKey: ['ptz', deviceId],
    queryFn: () => call(api.GET('/api/ipcams/ptz/{deviceId}', { params: { path: { deviceId } } })),
    enabled: canControl,
    staleTime: 5 * 60_000,
    retry: false,
  })
  const [error, setError] = useState<string | null>(null)
  const timer = useRef<number | null>(null)
  const [touring, setTouring] = useState(false)
  const tourTimer = useRef<number | null>(null)
  const [tourIndex, setTourIndex] = useState(0)
  const [pause, setPause] = useState(() => loadTourPause())
  const pauseNow = useRef(pause) // a pause picked during a tour counts from the next preset
  const [choosingPause, setChoosingPause] = useState(false)
  const presets = tourPresets(state.data?.presets ?? [])

  const send = useCallback(async (body: PtzRequest) => {
    try {
      await call(api.POST('/api/ipcams/ptz/{deviceId}', { params: { path: { deviceId } }, body }))
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }, [deviceId])

  const stopTour = useCallback(() => {
    if (tourTimer.current !== null) window.clearTimeout(tourTimer.current)
    tourTimer.current = null
    setTouring(false)
  }, [])

  const release = useCallback(() => {
    if (timer.current === null) return
    window.clearInterval(timer.current)
    timer.current = null
    void send({ op: 'stop' })
  }, [send])

  const hold = (direction: PtzDirection) => {
    onTouch?.()
    stopTour() // a hand on the arrows takes over from the tour
    if (timer.current !== null) window.clearInterval(timer.current)
    const move = ptzMove(direction)
    void send(move)
    timer.current = window.setInterval(() => void send(move), ptzRepeatMs)
  }

  const startTour = () => {
    onTouch?.()
    stopTour()
    setTouring(true)
    const step = (index: number) => {
      setTourIndex(index)
      void send({ op: 'preset', preset: presets[index].token }) // a failed preset is shown and skipped, the tour goes on
      tourTimer.current = window.setTimeout(() => step(nextTourIndex(index, presets.length)), tourStepMs(pauseNow.current))
    }
    step(0)
  }

  // Another camera picked, the widget closed or the page left while a button was held or the tour ran: everything stops.
  useEffect(() => () => {
    release()
    stopTour()
  }, [release, stopTour])

  if (!canControl || !state.data?.available) return null

  const button = (direction: PtzDirection, label: string, icon: ReactNode) => (
    <button type="button" aria-label={label} title={label}
      className="flex size-8 items-center justify-center rounded-md bg-black/55 text-neutral-100 hover:bg-black/75 active:bg-amber-600/80"
      onPointerDown={(e) => { e.currentTarget.setPointerCapture(e.pointerId); hold(direction) }}
      onPointerUp={release} onPointerCancel={release} onLostPointerCapture={release}
      onContextMenu={(e) => e.preventDefault()}>
      {icon}
    </button>
  )
  const chip = 'rounded-md bg-black/55 px-2 py-1 text-xs text-neutral-100 hover:bg-black/75'

  return (
    <div className="pointer-events-none absolute inset-x-2 bottom-2 flex items-end justify-between gap-2">
      <div className="pointer-events-auto grid touch-none select-none grid-cols-3 gap-1" aria-label={t('video.ptz.title')}>
        <span />{button('up', t('video.ptz.up'), <ChevronUp className="size-5" />)}<span />
        {button('left', t('video.ptz.left'), <ChevronLeft className="size-5" />)}
        <span />
        {button('right', t('video.ptz.right'), <ChevronRight className="size-5" />)}
        <span />{button('down', t('video.ptz.down'), <ChevronDown className="size-5" />)}<span />
      </div>
      <div className="pointer-events-auto flex flex-col items-end gap-1">
        {error && <p className="max-w-64 rounded bg-black/70 px-2 py-0.5 text-[11px] text-red-300">{t('video.ptz.failed')}: {error}</p>}
        <div className="flex items-center gap-1">
          <select className={`${chip} h-8 border-0`} aria-label={t('video.ptz.preset')} value=""
            onChange={(e) => {
              if (!e.target.value) return
              onTouch?.()
              stopTour()
              void send({ op: 'preset', preset: e.target.value })
            }}>
            <option value="">{t('video.ptz.preset')}</option>
            {presets.map((p) => <option key={p.token} value={p.token}>{p.name}</option>)}
          </select>
          <div className="relative">
            {choosingPause && (
              <div className="absolute bottom-full right-0 mb-1 flex flex-col gap-0.5 rounded-md bg-black/85 p-1" role="radiogroup" aria-label={t('video.ptz.pause')}>
                <p className="px-1 text-[10px] text-neutral-400">{t('video.ptz.pause')}</p>
                {tourPauses.map((s) => (
                  <button key={s} type="button" role="radio" aria-checked={pause === s}
                    className={pause === s ? 'rounded bg-amber-600/80 px-2 py-0.5 text-xs text-neutral-50' : 'rounded px-2 py-0.5 text-xs text-neutral-200 hover:bg-neutral-700'}
                    onClick={() => { setPause(s); pauseNow.current = s; saveTourPause(s); setChoosingPause(false) }}>
                    {t('video.ptz.seconds').replace('{s}', String(s))}
                  </button>
                ))}
              </div>
            )}
            <button type="button" className={`${chip} h-8`} title={t('video.ptz.pause')} aria-expanded={choosingPause}
              onClick={() => setChoosingPause((v) => !v)}>
              {t('video.ptz.seconds').replace('{s}', String(pause))}
            </button>
          </div>
          <button type="button" className={`${chip} flex h-8 items-center gap-1 ${touring ? 'bg-amber-600/80' : ''}`}
            title={touring ? t('video.ptz.tourStop') : t('video.ptz.tour')} aria-pressed={touring}
            onClick={() => (touring ? stopTour() : startTour())}>
            {touring ? <Square className="size-3.5" /> : <Play className="size-3.5" />}
            {touring ? presets[tourIndex]?.name : t('video.ptz.tourShort')}
          </button>
        </div>
        <div className="flex touch-none select-none gap-1">
          {button('zoomOut', t('video.ptz.zoomOut'), <ZoomOut className="size-4" />)}
          {button('zoomIn', t('video.ptz.zoomIn'), <ZoomIn className="size-4" />)}
        </div>
      </div>
    </div>
  )
}
