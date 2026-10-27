import { useMe, useRetention } from '@/api/queries'
import { Card, CardContent, CardHeader, CardTitle, Skeleton } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { formatAgo, formatBytes } from '@/lib/format'
import { cn } from '@/lib/utils'

/** Disk and retention (spec 7.4): how full the disk is, when the last cleanup ran, how long things are kept. */
export function RetentionCard() {
  const me = useMe()
  const isAdmin = me.data?.roles.includes('admin') ?? false
  const retention = useRetention(isAdmin)
  if (!isAdmin) return null
  const r = retention.data
  const used = r?.diskUsedPct ?? null
  const high = r?.diskHighPct ?? 85
  // Photos and the database are measured; the rest of the used space is the system, old hive versions, backups.
  const total = r?.diskTotalBytes ?? 0
  const usedBytes = r?.diskUsedBytes ?? null
  const full = used != null && used >= high
  const parts = r && total > 0 && usedBytes != null
    ? [
        { key: 'photos', bytes: Math.min(r.photoBytes, usedBytes), color: 'bg-amber-400' },
        { key: 'database', bytes: Math.min(r.databaseBytes, Math.max(0, usedBytes - r.photoBytes)), color: 'bg-sky-500' },
        { key: 'other', bytes: Math.max(0, usedBytes - r.photoBytes - r.databaseBytes), color: full ? 'bg-red-500' : 'bg-neutral-500' },
      ]
    : null
  const free = Math.max(0, total - (usedBytes ?? 0))
  // Above the limit and mostly not photos: deleting photos will hardly help.
  const notPhotos = parts != null && full && parts[2].bytes > parts[0].bytes

  return (
    <Card>
      <CardHeader><CardTitle>{t('retention.title')}</CardTitle></CardHeader>
      <CardContent className="space-y-3 text-sm">
        {retention.isLoading && <Skeleton className="h-24" />}
        {r && (
          <>
            <div>
              <div className="flex items-baseline justify-between">
                <span className="text-neutral-400">{t('retention.disk')}</span>
                <span className={cn('tabular-nums', used != null && used >= high ? 'text-red-400' : 'text-neutral-200')}>{used != null ? `${used}%` : '—'}</span>
              </div>
              {parts ? (
                <div className="mt-1 flex h-2 overflow-hidden rounded-full bg-neutral-800">
                  {parts.map((p) => <div key={p.key} className={cn('h-full', p.color)} style={{ width: `${(100 * p.bytes) / total}%` }} />)}
                </div>
              ) : (
                <div className="mt-1 h-2 overflow-hidden rounded-full bg-neutral-800">
                  <div className={cn('h-full', used != null && used >= high ? 'bg-red-500' : 'bg-amber-400')} style={{ width: `${Math.min(100, used ?? 0)}%` }} />
                </div>
              )}
              {parts && (
                <ul className="mt-2 space-y-0.5 text-xs">
                  {[...parts, { key: 'free', bytes: free, color: 'bg-neutral-800' }].map((p) => (
                    <li key={p.key} className="flex items-center gap-2">
                      <span className={cn('inline-block h-2 w-2 shrink-0 rounded-full', p.color)} />
                      <span className="text-neutral-400">{t(`retention.part.${p.key}`)}</span>
                      <span className="ml-auto tabular-nums text-neutral-200">{formatBytes(p.bytes)}</span>
                    </li>
                  ))}
                </ul>
              )}
              {notPhotos && <p className="mt-2 text-xs text-amber-300">{t('retention.notPhotos')}</p>}
              <p className="mt-1 text-xs text-neutral-500">
                {t('retention.diskHint')
                  .replace('{n}', String(high))
                  .replace('{every}', String(r.settings.diskCheckMinutes ?? 15))
                  .replace('{target}', String(r.settings.diskTargetPct ?? 80))
                  .replace('{floor}', String(r.settings.alarmFloorDays ?? 30))}
              </p>
            </div>

            <p className="text-neutral-300">
              {r.lastRunAt
                ? t('retention.lastRun').replace('{when}', formatAgo(r.lastRunAt)).replace('{photos}', String(r.lastPhotosDeleted)).replace('{size}', formatBytes(r.lastPhotoBytes))
                : t('retention.neverRun')}
            </p>

            <ul className="space-y-0.5 text-xs text-neutral-400">
              <li>{t('retention.keepAlarm').replace('{n}', String(r.settings.alarmPhotoDays))}</li>
              <li>{t('retention.keepOther').replace('{n}', String(r.settings.otherPhotoDays))}</li>
              <li>{t('retention.keepPinned')}</li>
              <li>{t('retention.keepRaw').replace('{n}', String(r.settings.rawTelemetryDays))}</li>
              <li>{t('retention.keepLogs').replace('{health}', String(r.settings.healthDays)).replace('{logs}', String(r.settings.logDays))}</li>
            </ul>
          </>
        )}
      </CardContent>
    </Card>
  )
}
