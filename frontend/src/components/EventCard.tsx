import { Check, ImageOff } from 'lucide-react'
import type { EventDto } from '@/api/client'
import { useAckEvent } from '@/api/queries'
import { Button } from '@/components/ui/button'
import { Badge, type BadgeTone } from '@/components/ui/primitives'
import { eventTypeLabel, severityLabel, t } from '@/i18n'
import { formatWhen } from '@/lib/format'
import { cn } from '@/lib/utils'

const severityTone: Record<string, BadgeTone> = { info: 'neutral', warn: 'warn', alarm: 'alarm', critical: 'critical' }

export type PhotoRef = { id: string; url: string; thumbUrl: string }

/** One row of the event feed: what, where, when, photo strip, acknowledge. */
export function EventCard({ event, onOpenPhoto, compact = false }: {
  event: EventDto
  onOpenPhoto?: (photos: PhotoRef[], index: number) => void
  compact?: boolean
}) {
  const ack = useAckEvent()
  const alarm = event.severity === 'alarm' || event.severity === 'critical'
  const expectedPhotos = (event.payload as { photos?: unknown[] } | null)?.photos?.length ?? 0

  return (
    <article className={cn('rounded-xl border bg-neutral-900/70 p-3', alarm && !event.acknowledgedAt ? 'border-red-900/70' : 'border-neutral-800')}>
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <span className="font-medium">{eventTypeLabel(event.type)}</span>
            <Badge tone={severityTone[event.severity] ?? 'neutral'}>{severityLabel(event.severity)}</Badge>
          </div>
          <p className="mt-0.5 truncate text-sm text-neutral-400">
            {event.zone ? `${event.zone} · ` : ''}
            {event.deviceName ?? event.deviceId} · {formatWhen(event.ts)}
          </p>
        </div>
        {alarm && !compact && (
          event.acknowledgedAt ? (
            <span className="flex shrink-0 items-center gap-1 text-xs text-emerald-400">
              <Check className="size-3.5" /> {t('events.acked')}
            </span>
          ) : (
            <Button size="sm" variant="secondary" disabled={ack.isPending} onClick={() => ack.mutate(event.id)}>
              {t('events.ack')}
            </Button>
          )
        )}
      </div>

      {event.photos.length > 0 ? (
        <div className="mt-3 flex gap-2 overflow-x-auto">
          {event.photos.map((p, i) => (
            <button
              key={p.id}
              type="button"
              onClick={() => onOpenPhoto?.(event.photos, i)}
              className="shrink-0 overflow-hidden rounded-lg border border-neutral-800 focus-visible:ring-2 focus-visible:ring-amber-400"
            >
              <img src={p.thumbUrl} alt="" loading="lazy" className={cn('object-cover', compact ? 'h-16 w-24' : 'h-24 w-32 sm:h-28 sm:w-40')} />
            </button>
          ))}
        </div>
      ) : (
        expectedPhotos > 0 && (
          <p className="mt-2 flex items-center gap-1 text-xs text-neutral-500">
            <ImageOff className="size-3.5" />
            {// Hornets retry uploads from the SD card; after a few minutes it is worth saying so plainly.
            Date.now() - new Date(event.receivedAt).getTime() < 5 * 60_000 ? t('events.photosPending') : t('events.photosMissing')}
          </p>
        )
      )}
    </article>
  )
}
