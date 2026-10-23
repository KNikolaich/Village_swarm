import { useQueryClient } from '@tanstack/react-query'
import { Bot, Camera, ImageOff, Lightbulb, Power, PowerOff } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router'
import { toast } from 'sonner'
import { api, call, type DeviceDto, type LatestValueDto } from '@/api/client'
import { useLatestPhoto, useRules, useSendCommand, useSnapshot } from '@/api/queries'
import { Lightbox } from '@/components/Lightbox'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { bannerKind, rulesFor, shownKeys } from '@/lib/dashboard'
import { formatWhen } from '@/lib/format'
import { relayChannels } from '@/lib/swarm'
import { cn } from '@/lib/utils'

const relayOn = (d: DeviceDto, channel: string) => (d.state as Record<string, unknown> | null)?.[channel] === 'on'

/** A camera: its newest photo with the time, and (as ticked) a snapshot button and its light (the camera's relay output). */
export function CameraBanner({ device, shown, canControl }: { device: DeviceDto; shown: Set<string>; canControl: boolean }) {
  const qc = useQueryClient()
  const photo = useLatestPhoto(device.deviceId)
  const send = useSendCommand(device.deviceId)
  const takeSnapshot = useSnapshot(device.deviceId)
  const [open, setOpen] = useState(false)
  const online = device.status === 'online'
  const channel = relayChannels(device)[0]
  const lit = relayOn(device, channel)
  const p = photo.data

  async function snapshot() {
    // The photo comes over HTTP a few seconds later; a failure is shown with its reason.
    for (const ms of [4000, 12_000]) setTimeout(() => void qc.invalidateQueries({ queryKey: ['media', 'latest', device.deviceId] }), ms)
    await takeSnapshot()
  }

  return (
    <div className="flex gap-3 rounded-lg bg-neutral-800/50 p-3">
      <div className="min-w-0 flex-1 space-y-2">
        <p className="truncate text-sm">{device.name}<span className="text-neutral-500"> · {p ? formatWhen(p.ts) : t('dashboard.noPhoto')}</span></p>
        {p ? (
          <button type="button" onClick={() => setOpen(true)} className="block w-full overflow-hidden rounded-md bg-neutral-900">
            <img src={p.thumbUrl} alt={device.name} className="aspect-video w-full object-cover" />
          </button>
        ) : (
          <div className="flex aspect-video w-full items-center justify-center rounded-md bg-neutral-900 text-neutral-600"><ImageOff className="size-6" /></div>
        )}
      </div>
      {canControl && (shown.has('snapshot') || shown.has('light')) && (
        <div className="flex shrink-0 flex-col justify-center gap-3 pt-6">
          {shown.has('snapshot') && (
            <Button variant="outline" size="icon" disabled={!online} title={t('device.snapshot')} aria-label={t('device.snapshot')} onClick={() => void snapshot()}><Camera /></Button>
          )}
          {shown.has('light') && (
            <Button
              variant="outline"
              size="icon"
              disabled={!online}
              title={lit ? t('dashboard.lightOff') : t('dashboard.lightOn')}
              aria-label={lit ? t('dashboard.lightOff') : t('dashboard.lightOn')}
              aria-pressed={lit}
              className={cn(lit && 'border-amber-400 bg-amber-400/15 text-amber-300')}
              onClick={() => void send('relay', { channel, set: lit ? 'off' : 'on' })}
            >
              <Lightbulb />
            </Button>
          )}
        </div>
      )}
      {open && p && <Lightbox photos={[p]} index={0} onIndex={() => {}} onClose={() => setOpen(false)} />}
    </div>
  )
}

/**
 * A relay or a heater: its state, a button per channel (a heater turns on for an hour, it keeps its own limits), and
 * "Авто", which switches the rules that drive the relay; pressing the relay by hand leaves "Авто" as it is.
 */
export function RelayBanner({ device, canControl, isAdmin }: { device: DeviceDto; canControl: boolean; isAdmin: boolean }) {
  const qc = useQueryClient()
  const send = useSendCommand(device.deviceId)
  const rules = rulesFor(useRules().data ?? [], device.deviceId)
  const online = device.status === 'online'
  const heater = device.features?.includes('heater') ?? device.type === 'heat'
  const auto = rules.some((r) => r.enabled)

  async function setAuto(enabled: boolean) {
    try {
      await Promise.all(rules.map((r) => call(api.PUT('/api/rules/{id}/enabled', { params: { path: { id: r.id } }, body: { enabled } }))))
    } catch {
      toast.error(t('dashboard.autoFailed'))
    }
    await qc.invalidateQueries({ queryKey: ['rules'] })
  }

  return (
    <div className="space-y-2 rounded-lg bg-neutral-800/50 p-3">
      <p className="truncate text-sm">{device.name}</p>
      {relayChannels(device).map((channel, _, all) => {
        const on = relayOn(device, channel)
        const known = (device.state as Record<string, unknown> | null)?.[channel] != null
        return (
          <div key={channel} className="flex items-center justify-between gap-2">
            <div className="flex min-w-0 items-center gap-2">
              {all.length > 1 && <span className="text-xs text-neutral-500">{channel}</span>}
              <Badge tone={on ? 'ok' : 'neutral'}>{on ? t('climate.heaterOn') : known ? t('climate.heaterOff') : t('climate.heaterUnknown')}</Badge>
            </div>
            {canControl && (
              <Button
                size="sm"
                variant={on ? 'secondary' : 'outline'}
                disabled={!online}
                onClick={() => void send('relay', on ? { channel, set: 'off' } : { channel, set: 'on', ...(heater ? { for_s: 3600 } : {}) })}
              >
                {on ? <><PowerOff />{t('climate.heaterOff2')}</> : <><Power />{heater ? t('climate.heaterOnHour') : t('device.relayOn')}</>}
              </Button>
            )}
          </div>
        )
      })}
      {rules.length > 0 ? (
        <div className="flex items-center justify-between gap-2 border-t border-neutral-700/60 pt-2">
          <p className="min-w-0 truncate text-[11px] text-neutral-500">
            {rules.map((r, i) => (
              <span key={r.id}>{i > 0 && ', '}<Link to={`/rules/${r.id}`} className={cn('hover:text-amber-400', !r.enabled && 'line-through')}>{r.name}</Link></span>
            ))}
          </p>
          <Button
            size="sm"
            variant="outline"
            disabled={!canControl}
            aria-pressed={auto}
            className={cn('shrink-0', auto && 'border-emerald-500/70 bg-emerald-500/15 text-emerald-300')}
            onClick={() => void setAuto(!auto)}
          >
            <Bot />{auto ? t('dashboard.autoOn') : t('dashboard.autoOff')}
          </Button>
        </div>
      ) : isAdmin && (
        <Link to="/rules" className="block text-[11px] text-amber-400">{t('dashboard.autoSetup')}</Link>
      )}
    </div>
  )
}

/** The banners of a zone: cameras and relays, as each hornet is ticked (a camera with no button still shows its photo). */
export function ZoneBanners({ devices, latest, canControl, isAdmin }: {
  devices: readonly DeviceDto[]
  latest: readonly LatestValueDto[]
  canControl: boolean
  isAdmin: boolean
}) {
  const banners = devices.flatMap((d) => {
    const kind = bannerKind(d, latest)
    return kind ? [{ d, kind, shown: shownKeys(d, latest) }] : []
  })
  if (banners.length === 0) return null
  return (
    <div className="@container">
      <div className="grid grid-cols-1 gap-3 @xl:grid-cols-2 @4xl:grid-cols-3">
        {banners.map(({ d, kind, shown }) => kind === 'camera'
          ? <CameraBanner key={d.deviceId} device={d} shown={shown} canControl={canControl} />
          : <RelayBanner key={d.deviceId} device={d} canControl={canControl} isAdmin={isAdmin} />)}
      </div>
    </div>
  )
}
