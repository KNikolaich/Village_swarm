import { useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, Square, Timer } from 'lucide-react'
import { useState } from 'react'
import { toast } from 'sonner'
import { api, type DeviceDto, type TimelapseDto } from '@/api/client'
import { keys, useTimelapse } from '@/api/queries'
import { Lightbox } from '@/components/Lightbox'
import { Button } from '@/components/ui/button'
import { Badge, Select } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { formatTime, formatWhen } from '@/lib/format'
import { intervalLabel, timelapseIntervals } from '@/lib/timelapse'

/**
 * Timelapse on a camera's page: a photo every so often, taken by the hive's schedule, with the camera's floodlight on for
 * each shot when ticked. The hive switches the light off once the photo is in or late; the camera does it by itself too.
 */
export function TimelapsePanel({ device, canControl, hasLight }: { device: DeviceDto; canControl: boolean; hasLight: boolean }) {
  const qc = useQueryClient()
  const timelapse = useTimelapse()
  const running = (timelapse.data ?? []).find((x) => x.deviceId === device.deviceId)
  const [editing, setEditing] = useState(false)

  if (!running && !canControl) return null
  return (
    <div className="space-y-3 text-sm">
      {running ? <Running timelapse={running} /> : <p className="text-xs text-neutral-500">{t('timelapse.hint')}</p>}
      {canControl && (!running || editing) && (
        <TimelapseForm
          key={running?.startedAt ?? 'new'}
          deviceId={device.deviceId}
          running={running}
          hasLight={hasLight}
          onDone={async () => {
            setEditing(false)
            await qc.invalidateQueries({ queryKey: keys.timelapse })
          }}
        />
      )}
      {canControl && running && !editing && (
        <div className="flex flex-wrap gap-2">
          <Button variant="outline" onClick={() => setEditing(true)}>{t('timelapse.change')}</Button>
          <Button
            variant="outline"
            onClick={async () => {
              const { response } = await api.DELETE('/api/timelapse/{deviceId}', { params: { path: { deviceId: device.deviceId } } })
              if (!response.ok && response.status !== 404) {
                toast.error(t('errors.generic'))
                return
              }
              toast.success(t('timelapse.stopped'))
              await qc.invalidateQueries({ queryKey: keys.timelapse })
            }}
          >
            <Square />{t('timelapse.stop')}
          </Button>
        </div>
      )}
    </div>
  )
}

function Running({ timelapse: x }: { timelapse: TimelapseDto }) {
  const [open, setOpen] = useState(false)
  const photo = x.lastPhotoId && x.lastPhotoAt
    ? { id: x.lastPhotoId, url: `/api/media/${x.lastPhotoId}`, thumbUrl: `/api/media/${x.lastPhotoId}/thumb`, ts: x.lastPhotoAt }
    : null
  // A failure older than the last frame is history: only a fresh one is worth a warning.
  const failedLast = x.lastFailedAt && x.lastError && (!x.lastPhotoAt || x.lastFailedAt > x.lastPhotoAt)
  return (
    <div className="space-y-2">
      <div className="flex flex-wrap items-center gap-2">
        <Badge tone="info"><Timer className="size-3" />{t('timelapse.on')}</Badge>
        <span>
          {intervalLabel(x.intervalMin)}
          {x.light && ` · ${t('timelapse.withLight')}`}
          {' · '}
          {x.shooting ? t('timelapse.shooting') : t('timelapse.next').replace('{time}', formatTime(x.nextAt))}
        </span>
      </div>
      <div className="flex items-center gap-3">
        {photo && (
          <button type="button" onClick={() => setOpen(true)} className="shrink-0 overflow-hidden rounded-lg border border-neutral-800">
            <img src={photo.thumbUrl} alt="" className="h-16 w-24 object-cover" />
          </button>
        )}
        <p className="text-xs text-neutral-400">
          {photo ? t('timelapse.lastPhoto').replace('{when}', formatWhen(photo.ts)) : t('timelapse.noPhotoYet')}
        </p>
      </div>
      {failedLast && (
        <p className="flex items-start gap-2 rounded-md bg-amber-500/15 px-3 py-2 text-amber-300">
          <AlertTriangle className="mt-0.5 size-4 shrink-0" />
          <span>
            {t('timelapse.failed').replace('{when}', formatWhen(x.lastFailedAt!)).replace('{why}', t(`timelapse.why.${x.lastError}`, x.lastError!))}
            {x.lastErrorDetail && ` (${x.lastErrorDetail})`}
            {x.failures > 1 && ` · ${t('timelapse.failures').replace('{n}', String(x.failures))}`}
            <span className="block text-xs text-amber-300/80">{t('timelapse.retryHint')}</span>
          </span>
        </p>
      )}
      {open && photo && <Lightbox photos={[photo]} index={0} onIndex={() => {}} onClose={() => setOpen(false)} />}
    </div>
  )
}

function TimelapseForm({ deviceId, running, hasLight, onDone }: {
  deviceId: string
  running?: TimelapseDto
  hasLight: boolean
  onDone: () => Promise<void>
}) {
  const [intervalMin, setIntervalMin] = useState(running?.intervalMin ?? 60)
  const [light, setLight] = useState(running?.light ?? false)
  const [busy, setBusy] = useState(false)

  async function save() {
    setBusy(true)
    try {
      const { response, error } = await api.PUT('/api/timelapse/{deviceId}', {
        params: { path: { deviceId } },
        body: { intervalMin, light: hasLight && light },
      })
      if (!response.ok) {
        const reason = (error as { errors?: Record<string, string[]> } | undefined)?.errors?.timelapse?.[0]
        toast.error(reason ?? t('errors.generic'))
        return
      }
      toast.success(running ? t('timelapse.changed') : t('timelapse.started'))
      await onDone()
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="space-y-3">
      <label className="block space-y-1">
        <span className="block text-xs text-neutral-400">{t('timelapse.every')}</span>
        <Select value={intervalMin} onChange={(e) => setIntervalMin(Number(e.target.value))} className="w-auto">
          {timelapseIntervals.map((m) => <option key={m} value={m}>{intervalLabel(m)}</option>)}
        </Select>
      </label>
      {hasLight && (
        <div className="space-y-1">
          <label className="flex items-center gap-2">
            <input type="checkbox" className="size-4 accent-amber-400" checked={light} onChange={(e) => setLight(e.target.checked)} />
            <span>{t('timelapse.light')}</span>
          </label>
          <p className="text-xs text-neutral-500">{t('timelapse.lightHint')}</p>
        </div>
      )}
      <Button disabled={busy} onClick={() => void save()}>
        <Timer />{running ? t('timelapse.apply') : t('timelapse.start')}
      </Button>
    </div>
  )
}
