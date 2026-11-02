import { useQuery } from '@tanstack/react-query'
import { ChevronDown, ChevronLeft, ChevronRight, ChevronUp, ZoomIn, ZoomOut } from 'lucide-react'
import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { api, call } from '@/api/client'
import { useMe } from '@/api/queries'
import { t } from '@/i18n'
import { ptzMove, ptzRepeatMs, type PtzDirection, type PtzRequest } from '@/lib/video'

/**
 * Arrows, zoom and presets over the picture of a camera that turns (contracts/ipcam.md "PTZ"), for users who may control.
 * A held button repeats its move every 400 ms and sends stop on release; if the stop is lost the hive stops the camera
 * itself a second after the last move. Nothing is shown for a camera without PTZ.
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

  const send = useCallback(async (body: PtzRequest) => {
    try {
      await call(api.POST('/api/ipcams/ptz/{deviceId}', { params: { path: { deviceId } }, body }))
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }, [deviceId])

  const release = useCallback(() => {
    if (timer.current === null) return
    window.clearInterval(timer.current)
    timer.current = null
    void send({ op: 'stop' })
  }, [send])

  const hold = (direction: PtzDirection) => {
    onTouch?.()
    if (timer.current !== null) window.clearInterval(timer.current)
    const move = ptzMove(direction)
    void send(move)
    timer.current = window.setInterval(() => void send(move), ptzRepeatMs)
  }

  // Another camera picked, the widget closed or the page left while a button was held: the camera stops.
  useEffect(() => release, [release])

  if (!canControl || !state.data?.available) return null

  const button = (direction: PtzDirection, label: string, icon: ReactNode, className = '') => (
    <button type="button" aria-label={label} title={label}
      className={`flex size-8 items-center justify-center rounded-md bg-black/55 text-neutral-100 hover:bg-black/75 active:bg-amber-600/80 ${className}`}
      onPointerDown={(e) => { e.currentTarget.setPointerCapture(e.pointerId); hold(direction) }}
      onPointerUp={release} onPointerCancel={release} onLostPointerCapture={release}
      onContextMenu={(e) => e.preventDefault()}>
      {icon}
    </button>
  )

  return (
    <div className="pointer-events-none absolute inset-x-2 bottom-2 flex items-end justify-between gap-2">
      <div className="pointer-events-auto grid touch-none select-none grid-cols-3 gap-1" aria-label={t('video.ptz.title')}>
        <span />{button('up', t('video.ptz.up'), <ChevronUp className="size-5" />)}<span />
        {button('left', t('video.ptz.left'), <ChevronLeft className="size-5" />)}
        <span />
        {button('right', t('video.ptz.right'), <ChevronRight className="size-5" />)}
        <span />{button('down', t('video.ptz.down'), <ChevronDown className="size-5" />)}<span />
      </div>
      <div className="pointer-events-auto flex max-w-[55%] flex-col items-end gap-1">
        {error && <p className="rounded bg-black/70 px-2 py-0.5 text-[11px] text-red-300">{t('video.ptz.failed')}: {error}</p>}
        {state.data.presets.length > 0 && (
          <div className="flex flex-wrap justify-end gap-1">
            {state.data.presets.map((p) => (
              <button key={p.token} type="button" title={t('video.ptz.preset')}
                className="rounded-md bg-black/55 px-2 py-1 text-xs text-neutral-100 hover:bg-black/75"
                onClick={() => { onTouch?.(); void send({ op: 'preset', preset: p.token }) }}>
                {p.name}
              </button>
            ))}
          </div>
        )}
        <div className="flex touch-none select-none gap-1">
          {button('zoomOut', t('video.ptz.zoomOut'), <ZoomOut className="size-4" />)}
          {button('zoomIn', t('video.ptz.zoomIn'), <ZoomIn className="size-4" />)}
        </div>
      </div>
    </div>
  )
}
