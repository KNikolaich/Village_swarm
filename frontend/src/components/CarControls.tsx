import { ArrowDown, ArrowLeft, ArrowRight, ArrowUp, ArrowUpLeft, ArrowUpRight, Lightbulb } from 'lucide-react'
import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { api, call } from '@/api/client'
import { useDevices, useMe, useSendCommand } from '@/api/queries'
import { t } from '@/i18n'
import { carDrive, driveRepeatMs, keyDirection, loadCarSpeed, saveCarSpeed, stopDrive, type CarDirection, type Drive } from '@/lib/car'

/**
 * Driving a car over its picture (contracts/mqtt-topics.md "Car"), for users who may control. A held arrow (or key) sends
 * its wheel speeds every 200 ms and a stop on release; the car stops by itself 500 ms after the last one, so a closed tab
 * or a lost connection never leaves it running. The speed is kept per browser; the light is the car's relay "light".
 */
export function CarControls({ deviceId, onTouch }: { deviceId: string; onTouch?: () => void }) {
  const me = useMe()
  const canControl = me.data?.roles.some((r) => r === 'admin' || r === 'member') ?? false
  const devices = useDevices()
  const light = (devices.data?.find((d) => d.deviceId === deviceId)?.state as { light?: string } | null)?.light === 'on'
  const sendCommand = useSendCommand(deviceId)
  const [speed, setSpeed] = useState(() => loadCarSpeed())
  const [error, setError] = useState<string | null>(null)
  const timer = useRef<number | null>(null)
  const held = useRef<CarDirection | null>(null)

  const send = useCallback(async (drive: Drive) => {
    try {
      await call(api.POST('/api/cars/{deviceId}/drive', { params: { path: { deviceId } }, body: drive }))
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }, [deviceId])

  const release = useCallback(() => {
    if (timer.current === null) return
    window.clearInterval(timer.current)
    timer.current = null
    held.current = null
    void send(stopDrive)
  }, [send])

  const hold = (direction: CarDirection) => {
    if (held.current === direction) return // a key repeating by itself
    onTouch?.()
    if (timer.current !== null) window.clearInterval(timer.current)
    held.current = direction
    const drive = carDrive(direction, speed)
    void send(drive)
    timer.current = window.setInterval(() => void send(drive), driveRepeatMs)
  }

  // Another camera picked, the widget closed or the page left while driving: the car stops.
  useEffect(() => release, [release])

  if (!canControl) return null

  const button = (direction: CarDirection, label: string, icon: ReactNode) => (
    <button type="button" aria-label={label} title={label}
      className="flex size-10 items-center justify-center rounded-md bg-black/55 text-neutral-100 hover:bg-black/75 active:bg-amber-600/80"
      onPointerDown={(e) => { e.currentTarget.setPointerCapture(e.pointerId); hold(direction) }}
      onPointerUp={release} onPointerCancel={release} onLostPointerCapture={release}
      onContextMenu={(e) => e.preventDefault()}>
      {icon}
    </button>
  )

  return (
    <div className="pointer-events-none absolute inset-x-2 bottom-2 flex items-end justify-between gap-2">
      <div className="pointer-events-auto grid touch-none select-none grid-cols-3 gap-1 outline-none" aria-label={t('car.controls')}
        tabIndex={0} title={t('car.keysHint')}
        onKeyDown={(e) => {
          const d = keyDirection(e.key)
          if (!d) return
          e.preventDefault()
          hold(d)
        }}
        onKeyUp={(e) => { if (keyDirection(e.key)) release() }}
        onBlur={release}>
        {button('forwardLeft', t('car.forwardLeft'), <ArrowUpLeft className="size-5" />)}
        {button('forward', t('car.forward'), <ArrowUp className="size-5" />)}
        {button('forwardRight', t('car.forwardRight'), <ArrowUpRight className="size-5" />)}
        {button('left', t('car.left'), <ArrowLeft className="size-5" />)}
        {button('back', t('car.back'), <ArrowDown className="size-5" />)}
        {button('right', t('car.right'), <ArrowRight className="size-5" />)}
      </div>
      <div className="pointer-events-auto flex flex-col items-end gap-1">
        {error && <p className="max-w-64 rounded bg-black/70 px-2 py-0.5 text-[11px] text-red-300">{t('car.failed')}: {error}</p>}
        <label className="flex items-center gap-2 rounded-md bg-black/55 px-2 py-1 text-xs text-neutral-100">
          {t('car.speed')}
          <input type="range" min={10} max={100} step={10} value={speed} aria-label={t('car.speed')} className="w-24 accent-amber-500"
            onChange={(e) => { const v = Number(e.target.value); setSpeed(v); saveCarSpeed(v) }} />
          <span className="w-8 tabular-nums">{speed}%</span>
        </label>
        <button type="button" aria-pressed={light} title={light ? t('car.lightOff') : t('car.lightOn')}
          className={`flex h-8 items-center gap-1 rounded-md px-2 text-xs ${light ? 'bg-amber-500/90 text-neutral-950' : 'bg-black/55 text-neutral-100 hover:bg-black/75'}`}
          onClick={() => { onTouch?.(); void sendCommand('relay', { channel: 'light', set: light ? 'off' : 'on', for_s: 600 }) }}>
          <Lightbulb className="size-4" />{t('car.light')}
        </button>
      </div>
    </div>
  )
}
