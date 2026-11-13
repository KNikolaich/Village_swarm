import { ChevronLeft, ChevronRight, CloudUpload, Pin, Play } from 'lucide-react'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate, useParams, useSearchParams } from 'react-router'
import type { MediaDto } from '@/api/client'
import { useDevices, useMe, useMediaDays, useMediaRange, useReplicaRange, useSendRangeToCity } from '@/api/queries'
import { Lightbox } from '@/components/Lightbox'
import { Button } from '@/components/ui/button'
import { Card, CardContent, Empty, Select, Skeleton } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { formatDayMonth, formatMonthYear, formatTime, isoDate, parseIsoDate } from '@/lib/format'
import { dayRange, inDayRange, isSingleDay, rangeFromUrl, type DayRange } from '@/lib/photoRange'
import { cn } from '@/lib/utils'

const weekdayKeys = ['mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun']

/** Screen 3 (spec 8.2): calendar → day or range of days → thumbnails by device → viewer. URL: /photos/YYYY-MM-DD[?to=YYYY-MM-DD]. */
export function PhotosPage() {
  const { date } = useParams()
  const navigate = useNavigate()
  // The camera can come in the address (?device=), from a camera's "Photo archive" button.
  const [searchParams] = useSearchParams()
  const selected = date ?? isoDate(new Date())
  const range = rangeFromUrl(selected, searchParams.get('to'))
  const [month, setMonth] = useState(() => {
    const d = parseIsoDate(selected)
    return new Date(d.getFullYear(), d.getMonth(), 1)
  })
  const [device, setDevice] = useState(searchParams.get('device') ?? '')
  const [pinnedOnly, setPinnedOnly] = useState(false)
  const devices = useDevices(true)
  // Only hornets that can have photos are offered: those with a camera role.
  const cameras = (devices.data ?? []).filter((d) => d.type === 'guard-cam' || d.type === 'ipcam')

  return (
    <div className="space-y-4 md:grid md:grid-cols-[20rem_1fr] md:gap-4 md:space-y-0">
      <div className="space-y-2">
        <h1 className="text-lg font-semibold">{t('photos.title')}</h1>
        {cameras.length > 1 && (
          <Select value={device} onChange={(e) => setDevice(e.target.value)} aria-label={t('photos.device')}>
            <option value="">{t('photos.allDevices')}</option>
            {cameras.map((c) => <option key={c.deviceId} value={c.deviceId}>{c.name}</option>)}
          </Select>
        )}
        <label className="flex items-center gap-2 text-sm text-neutral-300">
          <input type="checkbox" checked={pinnedOnly} onChange={(e) => setPinnedOnly(e.target.checked)} className="size-4 accent-amber-400" />
          {t('photos.pinnedOnly')}
        </label>
        <Calendar
          month={month}
          onMonth={setMonth}
          range={range}
          device={device || undefined}
          onRange={(r) => {
            const params = new URLSearchParams()
            if (!isSingleDay(r)) params.set('to', r.to)
            if (device) params.set('device', device)
            const search = params.toString()
            navigate({ pathname: `/photos/${r.from}`, search: search ? `?${search}` : '' })
          }}
        />
        <p className="text-xs text-neutral-500">{t('photos.rangeHint')}</p>
      </div>
      <Day range={range} device={device || undefined} pinnedOnly={pinnedOnly} />
    </div>
  )
}

function Calendar({ month, onMonth, range, device, onRange }: {
  month: Date
  onMonth: (d: Date) => void
  range: DayRange
  device?: string
  onRange: (r: DayRange) => void
}) {
  const first = month
  const last = new Date(month.getFullYear(), month.getMonth() + 1, 0)
  const days = useMediaDays(isoDate(first), isoDate(last), device)
  const counts = new Map((days.data ?? []).map((d) => [d.date, d.count]))
  const today = isoDate(new Date())
  const lead = (first.getDay() + 6) % 7 // Monday first

  // Press on the first day and release on the last (mouse or finger); a press and release on one day picks that day.
  const [drag, setDrag] = useState<{ anchor: string; current: string } | null>(null)
  const shown = drag ? dayRange(drag.anchor, drag.current) : range
  const dayAt = (x: number, y: number) => document.elementFromPoint(x, y)?.closest<HTMLElement>('[data-day]')?.dataset.day

  return (
    <Card>
      <CardContent>
        <div className="mb-2 flex items-center justify-between">
          <Button variant="ghost" size="icon" onClick={() => onMonth(new Date(month.getFullYear(), month.getMonth() - 1, 1))}><ChevronLeft /></Button>
          <span className="text-sm font-medium">{formatMonthYear(month)}</span>
          <Button variant="ghost" size="icon" onClick={() => onMonth(new Date(month.getFullYear(), month.getMonth() + 1, 1))}><ChevronRight /></Button>
        </div>
        <div
          className="grid select-none grid-cols-7 gap-1 text-center text-xs"
          style={{ touchAction: 'none' }}
          onPointerDown={(e) => {
            const day = dayAt(e.clientX, e.clientY)
            if (!day || (e.pointerType === 'mouse' && e.button !== 0)) return
            e.currentTarget.setPointerCapture(e.pointerId)
            setDrag({ anchor: day, current: day })
          }}
          onPointerMove={(e) => {
            const day = drag && dayAt(e.clientX, e.clientY)
            if (drag && day && day !== drag.current) setDrag({ ...drag, current: day })
          }}
          onPointerUp={(e) => {
            if (!drag) return
            onRange(dayRange(drag.anchor, dayAt(e.clientX, e.clientY) ?? drag.current))
            setDrag(null)
          }}
          onPointerCancel={() => setDrag(null)}
        >
          {weekdayKeys.map((w) => <span key={w} className="py-1 text-neutral-500">{t(`weekdays.${w}`)}</span>)}
          {Array.from({ length: lead }, (_, i) => <span key={`pad${i}`} />)}
          {Array.from({ length: last.getDate() }, (_, i) => {
            const iso = isoDate(new Date(month.getFullYear(), month.getMonth(), i + 1))
            const count = counts.get(iso) ?? 0
            const edge = iso === shown.from || iso === shown.to
            const inside = inDayRange(iso, shown)
            return (
              <button
                key={iso}
                type="button"
                data-day={iso}
                // a keyboard press has no pointer: it picks the day at once
                onClick={(e) => { if (e.detail === 0) onRange({ from: iso, to: iso }) }}
                className={cn(
                  'flex aspect-square flex-col items-center justify-center rounded-lg text-sm',
                  edge ? 'bg-amber-400 text-neutral-950' : inside ? 'bg-amber-400/30' : count > 0 ? 'bg-neutral-800 hover:bg-neutral-700' : 'text-neutral-500 hover:bg-neutral-900',
                  iso === today && !inside && 'ring-1 ring-amber-400/60',
                )}
              >
                {i + 1}
                {count > 0 && <span className={cn('text-[10px] leading-none', edge ? 'text-neutral-800' : 'text-amber-400')}>{count}</span>}
              </button>
            )
          })}
        </div>
      </CardContent>
    </Card>
  )
}

function Day({ range, device, pinnedOnly }: { range: DayRange; device?: string; pinnedOnly: boolean }) {
  const media = useMediaRange(range.from, range.to, device, pinnedOnly)
  const devices = useDevices(true)
  const multiDay = !isSingleDay(range)
  // The viewer opens on a list of photos and may start playing it as a timelapse.
  const [open, setOpen] = useState<{ photos: MediaDto[]; index: number; play: boolean } | null>(null)
  const items = useMemo(() => (media.data?.pages ?? []).flatMap((p) => p.items), [media.data])

  // Pages load as the end of the list comes into view; the button is for browsers that cannot tell.
  const end = useRef<HTMLDivElement>(null)
  const { hasNextPage, isFetchingNextPage, fetchNextPage } = media
  useEffect(() => {
    const node = end.current
    if (!node || !hasNextPage || typeof IntersectionObserver === 'undefined') return
    const observer = new IntersectionObserver((entries) => {
      if (entries.some((e) => e.isIntersecting) && !isFetchingNextPage) void fetchNextPage()
    }, { rootMargin: '400px' })
    observer.observe(node)
    return () => observer.disconnect()
  }, [hasNextPage, isFetchingNextPage, fetchNextPage, items.length])

  const groups = useMemo(() => {
    const byDevice = new Map<string, MediaDto[]>()
    for (const m of items) byDevice.set(m.deviceId, [...(byDevice.get(m.deviceId) ?? []), m])
    return [...byDevice.entries()].sort(([a], [b]) => a.localeCompare(b))
  }, [items])

  // Viewer pages through everything loaded in the same order as the grid.
  const ordered = groups.flatMap(([, list]) => list)
  const name = (id: string) => devices.data?.find((d) => d.deviceId === id)?.name ?? id
  const byTime = (list: MediaDto[]) => [...list].sort((a, b) => a.ts.localeCompare(b.ts))
  const stamp = (m: MediaDto) => (multiDay ? `${formatDayMonth(new Date(m.ts))} ${formatTime(m.ts)}` : formatTime(m.ts))

  return (
    <div className="space-y-4">
      <h2 className="text-base font-medium">
        {multiDay
          ? `${formatDayMonth(parseIsoDate(range.from))} – ${formatDayMonth(parseIsoDate(range.to))}`
          : range.from === isoDate(new Date()) ? t('photos.today') : formatDayMonth(parseIsoDate(range.from))}
        {items.length > 0 && <span className="ml-2 text-sm text-neutral-500">{items.length}{hasNextPage ? '+' : ''} {t('photos.photosCount')}</span>}
      </h2>
      <CityCopy range={range} device={device} pinnedOnly={pinnedOnly} />
      {media.isLoading && <Skeleton className="h-40" />}
      {!media.isLoading && items.length === 0 && <Empty>{t(multiDay ? 'photos.noPhotosRange' : 'photos.noPhotos')}</Empty>}
      {groups.map(([deviceId, list]) => (
        <section key={deviceId} className="space-y-2">
          <div className="flex items-center justify-between gap-2">
            <h3 className="text-sm text-neutral-400">{name(deviceId)}</h3>
            {list.length > 1 && (
              <Button variant="ghost" size="sm" onClick={() => setOpen({ photos: byTime(list), index: 0, play: true })}>
                <Play />{t('photos.timelapse')}
              </Button>
            )}
          </div>
          <div className="grid grid-cols-3 gap-1.5 sm:grid-cols-4 lg:grid-cols-5">
            {list.map((m) => (
              <button
                key={m.id}
                type="button"
                onClick={() => setOpen({ photos: ordered, index: ordered.indexOf(m), play: false })}
                className="group relative aspect-[4/3] overflow-hidden rounded-lg bg-neutral-900"
              >
                <img src={m.thumbUrl} alt="" loading="lazy" className="size-full object-cover transition group-hover:opacity-80" />
                <span className="absolute bottom-1 left-1 rounded bg-black/60 px-1 text-[10px] tabular-nums text-neutral-200">{stamp(m)}</span>
                {m.pinned && <Pin className="absolute right-1 top-1 size-3.5 text-amber-400" />}
              </button>
            ))}
          </div>
        </section>
      ))}
      {hasNextPage && (
        <div ref={end} className="flex justify-center">
          <Button variant="ghost" disabled={isFetchingNextPage} onClick={() => void fetchNextPage()}>{t('photos.loadMore')}</Button>
        </div>
      )}
      {open !== null && (
        <Lightbox
          photos={open.photos.map((m) => ({ ...m, caption: `${name(m.deviceId)} · ${stamp(m)}` }))}
          index={open.index}
          autoplay={open.play}
          onIndex={(index) => setOpen({ ...open, index })}
          onClose={() => setOpen(null)}
        />
      )}
    </div>
  )
}

/** "Догрузить в город": on the dacha with the city link set up, sends the shown photos to the city whatever their age. */
function CityCopy({ range, device, pinnedOnly }: { range: DayRange; device?: string; pinnedOnly: boolean }) {
  const me = useMe()
  const canControl = me.data?.roles.some((r) => r === 'admin' || r === 'member') ?? false
  const progress = useReplicaRange(range.from, range.to, device, pinnedOnly, canControl)
  const send = useSendRangeToCity()
  const p = progress.data
  if (!canControl || !p?.enabled || p.total === 0) return null
  const remaining = p.total - p.atCity
  return (
    <div className="flex flex-wrap items-center gap-3 rounded-lg bg-neutral-900 px-3 py-2 text-sm">
      <Button size="sm" variant="secondary" disabled={send.isPending || remaining === 0} onClick={() => send.mutate({ ...range, device, pinnedOnly })}>
        <CloudUpload />{t('photos.sendToCity')}
      </Button>
      <span className="text-neutral-400">
        {remaining === 0 ? t('photos.cityAll').replace('{n}', String(p.total)) : t('photos.cityProgress').replace('{done}', String(p.atCity)).replace('{total}', String(p.total)).replace('{waiting}', String(p.waiting))}
      </span>
    </div>
  )
}
