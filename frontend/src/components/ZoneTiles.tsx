import type { DeviceDto, LatestValueDto } from '@/api/client'
import { useRanges } from '@/api/queries'
import { zoneTiles } from '@/lib/dashboard'
import { formatAgo, formatValue } from '@/lib/format'
import { cn } from '@/lib/utils'
import { rangeClass, rangeKey, rangeState } from '@/lib/valueRange'

export function ZoneTiles({ devices, latest }: { devices: readonly DeviceDto[]; latest: readonly LatestValueDto[] }) {
  const tiles = zoneTiles(devices, latest)
  const ranges = useRanges().data
  if (tiles.length === 0) return null
  return (
    // Columns follow the width of the card, not of the window: a zone may be a third of a row on the overview.
    <div className="@container">
      <div className="grid grid-cols-2 gap-3 @md:grid-cols-3 @2xl:grid-cols-4 @5xl:grid-cols-6">
        {tiles.map((tile) => {
          const v = tile.main
          // The range someone set decides the colour; without one a temperature keeps the old limits (below 0, above 28).
          const range = ranges?.get(rangeKey(tile.device.deviceId, v.metric))
          const tone = range ? rangeClass(rangeState(v.value, range))
            : v.metric.startsWith('temperature') && v.value < 0 ? 'text-sky-300'
              : v.metric.startsWith('temperature') && v.value > 28 ? 'text-orange-300' : undefined
          return (
            <div key={tile.key} className="rounded-lg bg-neutral-800/50 p-3">
              <p className="truncate text-xs text-neutral-400">{tile.title}</p>
              <p className={cn('mt-1 text-2xl font-semibold tabular-nums', tone)}>{formatValue(v.metric, v.value)}</p>
              {tile.range && <p className="text-[11px] tabular-nums text-neutral-500/80">{tile.range}</p>}
              <p className="truncate text-xs text-neutral-500">{tile.note ? `${tile.note} · ` : ''}{formatAgo(v.ts)}</p>
            </div>
          )
        })}
      </div>
    </div>
  )
}
