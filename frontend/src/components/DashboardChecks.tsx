import { useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { api, call, type DeviceDto, type LatestValueDto } from '@/api/client'
import { keys } from '@/api/queries'
import { metricLabel, t } from '@/i18n'
import { dashboardItems, probeName, shownKeys, toggled } from '@/lib/dashboard'

/** A probe is named t1, t2... only when the hornet has several. */
const itemLabel = (key: string, kind: 'metric' | 'button', probes: boolean) =>
  kind === 'button' ? t(`dashboard.buttons.${key}`, key)
    : key.startsWith('temperature') && probes ? `${probeName(key)} · ${metricLabel(key)}`
      : metricLabel(key)

/**
 * Ticks for what the zone card and the overview show of a hornet: each reading (t1, t2, battery...) and each button
 * (snapshot, light, relay). The first tick starts from the defaults of its kind; "as by default" goes back to them.
 */
export function DashboardChecks({ device, latest, editable, compact }: {
  device: DeviceDto
  latest: readonly LatestValueDto[]
  editable: boolean
  /** In the zone editor: one line, no hint. */
  compact?: boolean
}) {
  const qc = useQueryClient()
  const items = dashboardItems(device, latest)
  const shown = shownKeys(device, latest)
  const probes = items.filter((i) => i.key.startsWith('temperature')).length > 1

  async function save(value: string[] | null) {
    try {
      await call(api.PUT('/api/devices/{deviceId}/dashboard', { params: { path: { deviceId: device.deviceId } }, body: { shown: value } }))
      await qc.invalidateQueries({ queryKey: keys.devices })
    } catch {
      toast.error(t('dashboard.failed'))
    }
  }

  if (items.length === 0) return compact ? null : <p className="text-sm text-neutral-500">{t('dashboard.nothing')}</p>
  return (
    <div className="space-y-2">
      {!compact && <p className="text-xs text-neutral-500">{t('dashboard.hint')}</p>}
      {/* Columns of one width, so the ticks of the hornets in a group line up under each other. */}
      <div className="grid grid-cols-[repeat(auto-fill,minmax(9.5rem,1fr))] gap-x-3 gap-y-1.5">
        {items.map((item) => (
          <label key={item.key} className="flex min-w-0 items-center gap-1.5 text-sm" title={itemLabel(item.key, item.kind, probes)}>
            <input
              type="checkbox"
              className="size-4 shrink-0 accent-amber-400"
              checked={shown.has(item.key)}
              disabled={!editable}
              onChange={(e) => void save(toggled(device, latest, item.key, e.target.checked))}
            />
            <span className="truncate">{itemLabel(item.key, item.kind, probes)}</span>
          </label>
        ))}
      </div>
      {editable && device.dashboard != null && (
        <button type="button" className="text-xs text-amber-400" onClick={() => void save(null)}>{t('dashboard.reset')}</button>
      )}
    </div>
  )
}
