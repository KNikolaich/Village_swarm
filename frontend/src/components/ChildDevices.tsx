import { ChevronDown, ChevronRight } from 'lucide-react'
import { Link } from 'react-router'
import { type DeviceDto } from '@/api/client'
import { CardContent } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { formatAgo } from '@/lib/format'
import { cn } from '@/lib/utils'
import { StatusIcon, QuickControls } from '@/components/DeviceReadings'

/** The devices that belong to a parent (an NVR's cameras), under its row; folded on a tap, open by default. */
export function ChildDevices({ devices, open, onToggle, canControl }: { devices: DeviceDto[]; open: boolean; onToggle: () => void; canControl: boolean }) {
  return (
    <CardContent className="border-t border-neutral-800 py-2">
      <button type="button" onClick={onToggle} aria-expanded={open}
        className="flex w-full items-center gap-1 text-xs text-neutral-400 hover:text-neutral-200">
        {open ? <ChevronDown className="size-3" /> : <ChevronRight className="size-3" />}
        {t('swarm.children').replace('{n}', String(devices.length))}
      </button>
      {open && (
        <ul className="mt-2 space-y-2 border-l border-neutral-800 pl-3">
          {devices.map((c) => (
            <li key={c.deviceId} className={cn('flex flex-wrap items-center gap-x-3 gap-y-1', c.status === 'disabled' && 'opacity-50')}>
              <StatusIcon device={c} />
              <div className="min-w-0 flex-1">
                <Link to={`/swarm/${encodeURIComponent(c.deviceId)}`} className="block truncate text-sm hover:text-amber-400">{c.name}</Link>
                <p className="truncate text-xs text-neutral-500">{c.deviceId}{c.zone ? ` · ${c.zone}` : ''} · {formatAgo(c.lastSeenAt)}</p>
              </div>
              {canControl && c.status === 'online' && <QuickControls device={c} />}
            </li>
          ))}
        </ul>
      )}
    </CardContent>
  )
}
