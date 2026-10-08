import { ChevronLeft, ChevronRight, Pin } from 'lucide-react'
import { useMemo, useState } from 'react'
import { useNavigate, useParams } from 'react-router'
import type { MediaDto } from '@/api/client'
import { useDevices, useMediaDay, useMediaDays } from '@/api/queries'
import { Lightbox } from '@/components/Lightbox'
import { Button } from '@/components/ui/button'
import { Card, CardContent, Empty, Skeleton } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { formatDayMonth, formatMonthYear, formatTime, isoDate, parseIsoDate } from '@/lib/format'
import { cn } from '@/lib/utils'

const weekdays = ['Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб', 'Вс']

/** Screen 3 (spec 8.2): calendar → day → thumbnails by device → viewer. URL: /photos/YYYY-MM-DD. */
export function PhotosPage() {
  const { date } = useParams()
  const navigate = useNavigate()
  const selected = date ?? isoDate(new Date())
  const [month, setMonth] = useState(() => {
    const d = parseIsoDate(selected)
    return new Date(d.getFullYear(), d.getMonth(), 1)
  })

  return (
    <div className="space-y-4 md:grid md:grid-cols-[20rem_1fr] md:gap-4 md:space-y-0">
      <div className="space-y-2">
        <h1 className="text-lg font-semibold">{t('photos.title')}</h1>
        <Calendar month={month} onMonth={setMonth} selected={selected} onSelect={(d) => navigate(`/photos/${d}`)} />
      </div>
      <Day date={selected} />
    </div>
  )
}

function Calendar({ month, onMonth, selected, onSelect }: {
  month: Date
  onMonth: (d: Date) => void
  selected: string
  onSelect: (iso: string) => void
}) {
  const first = month
  const last = new Date(month.getFullYear(), month.getMonth() + 1, 0)
  const days = useMediaDays(isoDate(first), isoDate(last))
  const counts = new Map((days.data ?? []).map((d) => [d.date, d.count]))
  const today = isoDate(new Date())
  const lead = (first.getDay() + 6) % 7 // Monday first

  return (
    <Card>
      <CardContent>
        <div className="mb-2 flex items-center justify-between">
          <Button variant="ghost" size="icon" onClick={() => onMonth(new Date(month.getFullYear(), month.getMonth() - 1, 1))}><ChevronLeft /></Button>
          <span className="text-sm font-medium">{formatMonthYear(month)}</span>
          <Button variant="ghost" size="icon" onClick={() => onMonth(new Date(month.getFullYear(), month.getMonth() + 1, 1))}><ChevronRight /></Button>
        </div>
        <div className="grid grid-cols-7 gap-1 text-center text-xs">
          {weekdays.map((w) => <span key={w} className="py-1 text-neutral-500">{w}</span>)}
          {Array.from({ length: lead }, (_, i) => <span key={`pad${i}`} />)}
          {Array.from({ length: last.getDate() }, (_, i) => {
            const iso = isoDate(new Date(month.getFullYear(), month.getMonth(), i + 1))
            const count = counts.get(iso) ?? 0
            return (
              <button
                key={iso}
                type="button"
                onClick={() => onSelect(iso)}
                className={cn(
                  'flex aspect-square flex-col items-center justify-center rounded-lg text-sm',
                  iso === selected ? 'bg-amber-400 text-neutral-950' : count > 0 ? 'bg-neutral-800 hover:bg-neutral-700' : 'text-neutral-500 hover:bg-neutral-900',
                  iso === today && iso !== selected && 'ring-1 ring-amber-400/60',
                )}
              >
                {i + 1}
                {count > 0 && <span className={cn('text-[10px] leading-none', iso === selected ? 'text-neutral-800' : 'text-amber-400')}>{count}</span>}
              </button>
            )
          })}
        </div>
      </CardContent>
    </Card>
  )
}

function Day({ date }: { date: string }) {
  const media = useMediaDay(date)
  const devices = useDevices()
  const [open, setOpen] = useState<number | null>(null)
  const items = useMemo(() => media.data ?? [], [media.data])

  const groups = useMemo(() => {
    const byDevice = new Map<string, MediaDto[]>()
    for (const m of items) byDevice.set(m.deviceId, [...(byDevice.get(m.deviceId) ?? []), m])
    return [...byDevice.entries()].sort(([a], [b]) => a.localeCompare(b))
  }, [items])

  // Viewer pages through the whole day in the same order as the grid.
  const ordered = groups.flatMap(([, list]) => list)
  const name = (id: string) => devices.data?.find((d) => d.deviceId === id)?.name ?? id

  return (
    <div className="space-y-4">
      <h2 className="text-base font-medium">
        {date === isoDate(new Date()) ? t('photos.today') : formatDayMonth(parseIsoDate(date))}
        {items.length > 0 && <span className="ml-2 text-sm text-neutral-500">{items.length} {t('photos.photosCount')}</span>}
      </h2>
      {media.isLoading && <Skeleton className="h-40" />}
      {!media.isLoading && items.length === 0 && <Empty>{t('photos.noPhotos')}</Empty>}
      {groups.map(([deviceId, list]) => (
        <section key={deviceId} className="space-y-2">
          <h3 className="text-sm text-neutral-400">{name(deviceId)}</h3>
          <div className="grid grid-cols-3 gap-1.5 sm:grid-cols-4 lg:grid-cols-5">
            {list.map((m) => (
              <button
                key={m.id}
                type="button"
                onClick={() => setOpen(ordered.indexOf(m))}
                className="group relative aspect-[4/3] overflow-hidden rounded-lg bg-neutral-900"
              >
                <img src={m.thumbUrl} alt="" loading="lazy" className="size-full object-cover transition group-hover:opacity-80" />
                <span className="absolute bottom-1 left-1 rounded bg-black/60 px-1 text-[10px] tabular-nums text-neutral-200">{formatTime(m.ts)}</span>
                {m.pinned && <Pin className="absolute right-1 top-1 size-3.5 text-amber-400" />}
              </button>
            ))}
          </div>
        </section>
      ))}
      {open !== null && (
        <Lightbox
          photos={ordered.map((m) => ({ ...m, caption: `${name(m.deviceId)} · ${formatTime(m.ts)}` }))}
          index={open}
          onIndex={setOpen}
          onClose={() => setOpen(null)}
        />
      )}
    </div>
  )
}
