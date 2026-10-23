import { Pencil } from 'lucide-react'
import { Link } from 'react-router'
import type { DeviceDto, LatestValueDto } from '@/api/client'
import { t } from '@/i18n'
import { zoneTiles } from '@/lib/dashboard'
import { formatAgo, formatValue } from '@/lib/format'
import { cn } from '@/lib/utils'

export function ZoneTiles({ devices, latest, editZone }: {
  devices: readonly DeviceDto[]
  latest: readonly LatestValueDto[]
  /** Set for an administrator: each tile gets a pencil to its hornet's card, which leads back to this zone. */
  editZone?: number
}) {
  const tiles = zoneTiles(devices, latest)
  if (tiles.length === 0) return null
  return (
    // Columns follow the width of the card, not of the window: a zone may be a third of a row on the overview.
    <div className="@container">
      <div className="grid grid-cols-2 gap-3 @md:grid-cols-3 @2xl:grid-cols-4 @5xl:grid-cols-6">
        {tiles.map((tile) => {
          const v = tile.main
          const cold = v.metric.startsWith('temperature') && v.value < 0
          const hot = v.metric.startsWith('temperature') && v.value > 28
          return (
            <div key={tile.key} className="group relative rounded-lg bg-neutral-800/50 p-3">
              <p className="truncate pr-6 text-xs text-neutral-400">{tile.title}</p>
              {editZone != null && (
                <Link
                  to={`/swarm/${tile.device.deviceId}?zone=${editZone}`}
                  title={t('climate.editHornet')}
                  aria-label={t('climate.editHornet')}
                  className="absolute right-2 top-2 text-neutral-500 hover:text-amber-400"
                >
                  <Pencil className="size-3.5" />
                </Link>
              )}
              <p className={cn('mt-1 text-2xl font-semibold tabular-nums', cold && 'text-sky-300', hot && 'text-orange-300')}>{formatValue(v.metric, v.value)}</p>
              {tile.range && <p className="text-[11px] tabular-nums text-neutral-500/80">{tile.range}</p>}
              <p className="truncate text-xs text-neutral-500">{tile.note ? `${tile.note} · ` : ''}{formatAgo(v.ts)}</p>
            </div>
          )
        })}
      </div>
    </div>
  )
}
