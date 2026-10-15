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
              <div className="mt-1 h-2 overflow-hidden rounded-full bg-neutral-800">
                <div className={cn('h-full', used != null && used >= high ? 'bg-red-500' : 'bg-amber-400')} style={{ width: `${Math.min(100, used ?? 0)}%` }} />
              </div>
              <p className="mt-1 text-xs text-neutral-500">{t('retention.diskHint').replace('{n}', String(high))}</p>
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
