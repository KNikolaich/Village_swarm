import { useState } from 'react'
import { Link } from 'react-router'
import type { DeviceDto, LatestValueDto } from '@/api/client'
import { useRanges } from '@/api/queries'
import { TileChartModal, TileTooltip } from '@/components/TileChartModal'
import { t } from '@/i18n'
import { zoneTiles, type Tile } from '@/lib/dashboard'
import { formatAgo, formatValue } from '@/lib/format'
import { isStale } from '@/lib/stale'
import { cn } from '@/lib/utils'
import { rangeClass, rangeKey, rangeState } from '@/lib/valueRange'

export function ZoneTiles({ devices, latest }: { devices: readonly DeviceDto[]; latest: readonly LatestValueDto[] }) {
  const tiles = zoneTiles(devices, latest)
  const ranges = useRanges().data
  const [open, setOpen] = useState<Tile | null>(null)
  if (tiles.length === 0) return null
  return (
    // Columns follow the width of the card, not of the window: a zone may be a third of a row on the overview.
    <div className="@container">
      <div className="grid grid-cols-2 gap-3 @md:grid-cols-3 @2xl:grid-cols-4 @5xl:grid-cols-6">
        {tiles.map((tile) => {
          // The range someone set decides the colour; without one a temperature keeps the old limits (below 0, above 28).
          const range = ranges?.get(rangeKey(tile.device.deviceId, tile.main.metric))
          const tone = range ? rangeClass(rangeState(tile.main.value, range))
            : tile.main.metric.startsWith('temperature') && tile.main.value < 0 ? 'text-sky-300'
              : tile.main.metric.startsWith('temperature') && tile.main.value > 28 ? 'text-orange-300' : undefined
          return <TileCard key={tile.key} tile={tile} tone={tone} onOpen={() => setOpen(tile)} />
        })}
      </div>
      {open && <TileChartModal device={open.device} metric={open.main.metric} title={open.title} onClose={() => setOpen(null)} />}
    </div>
  )
}

/**
 * One reading. Two places to click: the name (an underlined link to the hornet while the pointer is over it), and the
 * rest of the tile, which shows the chart in a tooltip on hover and opens it in a window on click. A reading that has
 * gone quiet (its value is old) is dimmed, so a silent hornet is seen at a glance.
 */
function TileCard({ tile, tone, onOpen }: { tile: Tile; tone?: string; onOpen: () => void }) {
  const [hover, setHover] = useState(false)
  const v = tile.main
  const stale = isStale(v.ts)
  return (
    <div className={cn('relative rounded-lg bg-neutral-800/50 transition-colors hover:bg-neutral-800', stale && 'bg-neutral-800/25')}>
      <button
        type="button"
        className="absolute inset-0 rounded-lg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-amber-400/60"
        aria-label={`${tile.title}: ${t('tileChart.open')}`}
        onClick={onOpen}
        onPointerEnter={(e) => setHover(e.pointerType === 'mouse')}
        onPointerLeave={() => setHover(false)}
      />
      <div className={cn('pointer-events-none relative p-3', stale && 'opacity-45')} title={stale ? t('tileChart.offline') : undefined}>
        <Link
          to={`/swarm/${encodeURIComponent(tile.device.deviceId)}`}
          className="pointer-events-auto block max-w-full truncate text-xs text-neutral-400 hover:text-amber-300 hover:underline"
        >
          {tile.title}
        </Link>
        <p className={cn('mt-1 text-2xl font-semibold tabular-nums', !stale && tone)}>{formatValue(v.metric, v.value)}</p>
        {tile.range && <p className="text-[11px] tabular-nums text-neutral-500/80">{tile.range}</p>}
        <p className="truncate text-xs text-neutral-500">{tile.note ? `${tile.note} · ` : ''}{formatAgo(v.ts)}</p>
      </div>
      {hover && (
        <div className="absolute left-0 top-full z-30 mt-1">
          <TileTooltip device={tile.device} metric={v.metric} title={tile.title} />
        </div>
      )}
    </div>
  )
}
