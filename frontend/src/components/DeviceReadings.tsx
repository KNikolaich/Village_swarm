import { useQueryClient } from '@tanstack/react-query'
import { BatteryMedium, Camera, Car, CircleHelp, Cpu, Droplets, Flame, Gauge, HardDrive, Lightbulb, Moon, Thermometer, Wifi, WifiOff, type LucideIcon } from 'lucide-react'
import { useState } from 'react'
import { useNavigate } from 'react-router'
import { type DeviceDto, type LatestValueDto } from '@/api/client'
import { keys, useRanges, useSendCommand, useSnapshot } from '@/api/queries'
import { Button } from '@/components/ui/button'
import { metricLabel, statusLabel, t } from '@/i18n'
import { formatAgo, formatValue } from '@/lib/format'
import { mainReadings, quickControls } from '@/lib/swarm'
import { cn } from '@/lib/utils'
import { driveLink } from '@/lib/video'
import { rangeClass, rangeKey, rangeState } from '@/lib/valueRange'
import { asleep } from '@/lib/deviceSleep'

const readingIcons: Record<string, LucideIcon> = {
  temperature: Thermometer,
  temperature_2: Thermometer,
  temperature_ntc: Thermometer,
  humidity: Droplets,
  pressure: Gauge,
  battery_voltage: BatteryMedium,
  cpu_temperature: Cpu,
  disk_used_pct: HardDrive,
}

/** A reading older than this is shown dimmed: the hornet has not confirmed it lately. */
const staleAfterMs = 3 * 3600_000

/** The hornet's main readings as large numbers; the label and age are in the tooltip. */
export function Readings({ device, latest }: { device: DeviceDto; latest: LatestValueDto[] }) {
  const readings = mainReadings(device, latest)
  const ranges = useRanges().data
  if (readings.length === 0) return null
  return (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-1">
      {readings.map((v) => {
        const Icon = readingIcons[v.metric]
        const stale = device.status !== 'online' || Date.now() - new Date(v.ts).getTime() > staleAfterMs
        return (
          <span key={v.metric} title={`${metricLabel(v.metric)} · ${formatAgo(v.ts)}`}
            className={cn('flex items-center gap-1', stale && 'opacity-50')}>
            {Icon && <Icon className="size-4 text-neutral-500" />}
            <span className={cn('text-xl font-semibold tabular-nums', ranges?.has(rangeKey(device.deviceId, v.metric))
              ? rangeClass(rangeState(v.value, ranges.get(rangeKey(device.deviceId, v.metric))))
              : v.metric.startsWith('temperature') && v.value < 0 && 'text-sky-300')}>
              {formatValue(v.metric, v.value)}
            </span>
          </span>
        )
      })}
    </div>
  )
}

/** Whether the hornet is on line, by one sign and its colour before the name; the word is in the tooltip. */
export function StatusIcon({ device }: { device: DeviceDto }) {
  const sleeping = asleep(device)
  const label = sleeping ? t('swarm.sleeping') : statusLabel(device.status)
  const Icon = sleeping ? Moon : device.status === 'online' ? Wifi : WifiOff
  const tone = sleeping ? 'text-sky-300' : device.status === 'online' ? 'text-emerald-400'
    : device.status === 'disabled' || device.status === 'pending' ? 'text-neutral-500' : 'text-red-400'
  return <span title={label} aria-label={label} role="img" className={cn('shrink-0', tone)}><Icon className="size-4" /></span>
}

/** "?" by the sleep controls: a tap opens what sleep, never sleeping, deep and light sleep mean (phones have no hover). */
export function SleepHelp() {
  const [open, setOpen] = useState(false)
  return (
    <span className="relative">
      <button type="button" aria-label={t('swarm.sleepHelpLabel')} aria-expanded={open} onClick={() => setOpen(!open)}
        className={cn('inline-flex align-middle', open ? 'text-amber-400' : 'text-neutral-500 hover:text-neutral-300')}>
        <CircleHelp className="size-3.5" />
      </button>
      {open && (
        <span role="tooltip" className="fixed inset-x-4 top-24 z-50 block whitespace-pre-line rounded-lg border border-neutral-700 bg-neutral-900 p-3 text-xs text-neutral-300 shadow-xl sm:absolute sm:inset-x-auto sm:left-0 sm:top-6 sm:z-20 sm:w-96"
          onClick={() => setOpen(false)}>
          {t('swarm.sleepHelp')}
        </span>
      )}
    </span>
  )
}

/** One-press controls of what the hornet drives: a relay toggles (the bulb is lit while it is on), a camera takes a photo. */
export function QuickControls({ device }: { device: DeviceDto }) {
  const qc = useQueryClient()
  const send = useSendCommand(device.deviceId)
  const takeSnapshot = useSnapshot(device.deviceId)
  const navigate = useNavigate()
  const { relays, snapshot, drive } = quickControls(device)
  if (relays.length === 0 && !snapshot && !drive) return null

  async function toggle(channel: string, on: boolean | null) {
    await send('relay', { channel, set: on ? 'off' : 'on' })
    // The new state comes back as the hornet's retained state; read it once it had time to arrive.
    for (const ms of [1500, 5000]) setTimeout(() => void qc.invalidateQueries({ queryKey: keys.devices }), ms)
  }

  return (
    <div className="flex items-center gap-1">
      {relays.map(({ channel, on }) => {
        const Icon = channel === 'heater' ? Flame : Lightbulb
        const title = t(on ? 'swarm.relayOnTitle' : on === false ? 'swarm.relayOffTitle' : 'swarm.relayUnknownTitle').replace('{channel}', channel)
        return (
          <Button key={channel} variant="outline" size="icon" title={title} aria-label={title} aria-pressed={on ?? undefined}
            className={on ? 'border-amber-400/60 bg-amber-400/15 text-amber-300 hover:bg-amber-400/25' : 'text-neutral-400'}
            onClick={() => void toggle(channel, on)}>
            <Icon className={on ? 'fill-amber-300/40' : undefined} />
          </Button>
        )
      })}
      {snapshot && (
        <Button variant="outline" size="icon" title={t('swarm.snapshot')} aria-label={t('swarm.snapshot')} onClick={() => void takeSnapshot()}>
          <Camera />
        </Button>
      )}
      {drive && (
        <Button variant="outline" size="icon" title={t('car.drive')} aria-label={t('car.drive')} onClick={() => void navigate(driveLink(device.deviceId))}>
          <Car />
        </Button>
      )}
    </div>
  )
}
