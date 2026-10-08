import { useEffect, useRef, useState } from 'react'
import { useSearchParams } from 'react-router'
import { useDevices, useEvents, type EventFilters } from '@/api/queries'
import { EventCard, type PhotoRef } from '@/components/EventCard'
import { Lightbox } from '@/components/Lightbox'
import { Button } from '@/components/ui/button'
import { Empty, Select, Skeleton } from '@/components/ui/primitives'
import { eventTypeLabel, severityLabel, t } from '@/i18n'

const types = ['motion', 'door_open', 'leak', 'failsafe', 'snapshot', 'config_applied']
const severities = ['warn', 'alarm', 'critical']

/** Screen 2 (spec 8.2): filtered feed with infinite scroll, photos and acknowledgement. Filters live in the URL. */
export function EventsPage() {
  const [params, setParams] = useSearchParams()
  const filters: EventFilters = {
    type: params.get('type') ?? undefined,
    device: params.get('device') ?? undefined,
    severity: params.get('severity') ?? undefined,
  }
  const devices = useDevices()
  const events = useEvents(filters)
  const [viewer, setViewer] = useState<{ photos: PhotoRef[]; index: number } | null>(null)
  const sentinel = useRef<HTMLDivElement>(null)

  const { hasNextPage, isFetchingNextPage, fetchNextPage } = events
  useEffect(() => {
    const el = sentinel.current
    if (!el) return
    const io = new IntersectionObserver(([entry]) => {
      if (entry.isIntersecting && hasNextPage && !isFetchingNextPage) void fetchNextPage()
    }, { rootMargin: '400px' })
    io.observe(el)
    return () => io.disconnect()
  }, [hasNextPage, isFetchingNextPage, fetchNextPage])

  const setFilter = (key: keyof EventFilters, value: string) => {
    const next = new URLSearchParams(params)
    if (value) next.set(key, value)
    else next.delete(key)
    setParams(next, { replace: true })
  }

  const items = events.data?.pages.flatMap((p) => p.items) ?? []

  return (
    <div className="space-y-3">
      <h1 className="text-lg font-semibold">{t('events.title')}</h1>
      <div className="flex flex-wrap gap-2">
        <Select value={filters.type ?? ''} onChange={(e) => setFilter('type', e.target.value)}>
          <option value="">{t('events.allTypes')}</option>
          {types.map((x) => <option key={x} value={x}>{eventTypeLabel(x)}</option>)}
        </Select>
        <Select value={filters.device ?? ''} onChange={(e) => setFilter('device', e.target.value)}>
          <option value="">{t('events.allDevices')}</option>
          {devices.data?.map((d) => <option key={d.deviceId} value={d.deviceId}>{d.name}</option>)}
        </Select>
        <Select value={filters.severity ?? ''} onChange={(e) => setFilter('severity', e.target.value)}>
          <option value="">{t('events.allSeverities')}</option>
          {severities.map((x) => <option key={x} value={x}>{severityLabel(x)}+</option>)}
        </Select>
      </div>

      {events.isLoading && Array.from({ length: 4 }, (_, i) => <Skeleton key={i} className="h-28" />)}
      {!events.isLoading && items.length === 0 && <Empty>{t('events.empty')}</Empty>}
      <div className="space-y-2">
        {items.map((e) => <EventCard key={e.id} event={e} onOpenPhoto={(photos, index) => setViewer({ photos, index })} />)}
      </div>
      <div ref={sentinel} />
      {hasNextPage && (
        <Button variant="secondary" className="w-full" disabled={isFetchingNextPage} onClick={() => void fetchNextPage()}>
          {t('events.loadMore')}
        </Button>
      )}

      {viewer && (
        <Lightbox photos={viewer.photos} index={viewer.index} onIndex={(index) => setViewer({ ...viewer, index })} onClose={() => setViewer(null)} />
      )}
    </div>
  )
}
