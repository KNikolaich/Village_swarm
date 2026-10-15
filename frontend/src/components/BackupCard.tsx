import { AlertTriangle, CheckCircle2 } from 'lucide-react'
import type { BackupRunDto } from '@/api/client'
import { useBackups, useMe } from '@/api/queries'
import { Badge, Card, CardContent, CardHeader, CardTitle, Skeleton } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { formatAgo, formatBytes, formatWhen } from '@/lib/format'

/** One line about the latest run of a kind: when, how big, whether it worked and why not. */
function Line({ label, run, missing }: { label: string; run: BackupRunDto | null; missing: string }) {
  return (
    <div className="flex items-start justify-between gap-3 py-2">
      <div className="min-w-0">
        <p className="text-sm">{label}</p>
        <p className="truncate text-xs text-neutral-500" title={run?.detail ?? undefined}>
          {run ? run.detail ?? '' : missing}
        </p>
      </div>
      {run && (
        <div className="shrink-0 text-right text-xs">
          <Badge tone={run.ok ? 'ok' : 'alarm'}>{run.ok ? t('backups.ok') : t('backups.failed')}</Badge>
          <p className="mt-1 text-neutral-500">{formatAgo(run.finishedAt)}{run.bytes != null && run.bytes > 0 ? ` · ${formatBytes(run.bytes)}` : ''}</p>
        </div>
      )}
    </div>
  )
}

/** Backups (spec 8.2 screen 9, 10.2): when the last dump and off-site copy were made, the weekly restore check, trouble. */
export function BackupCard() {
  const me = useMe()
  const isAdmin = me.data?.roles.includes('admin') ?? false
  const backups = useBackups(isAdmin)
  if (!isAdmin) return null
  const b = backups.data

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t('backups.title')}</CardTitle>
        {b && (b.ok
          ? <span className="flex items-center gap-1 text-xs text-emerald-400"><CheckCircle2 className="size-4" />{t('backups.allWell')}</span>
          : <span className="flex items-center gap-1 text-xs text-red-400"><AlertTriangle className="size-4" />{t('backups.trouble')}</span>)}
      </CardHeader>
      <CardContent className="space-y-3">
        {backups.isLoading && <Skeleton className="h-24" />}
        {b && (
          <>
            {b.problems.length > 0 && (
              <ul className="space-y-1 rounded-lg border border-red-900/60 bg-red-950/30 p-3 text-sm text-red-300">
                {b.problems.map((p) => <li key={p}>{t(`backups.problems.${p}`, p)}</li>)}
              </ul>
            )}
            <div className="divide-y divide-neutral-800">
              <Line label={t('backups.dump')} run={b.dump} missing={t('backups.noDump')} />
              <Line label={t('backups.offsite')} run={b.offsite} missing={t('backups.noOffsite')} />
              <Line label={t('backups.verify')} run={b.verify} missing={t('backups.noVerify')} />
            </div>
            {!b.offsite && <p className="text-xs text-amber-400">{t('backups.offsiteHint')}</p>}
            {b.recent.length > 0 && (
              <details className="text-xs text-neutral-400">
                <summary className="cursor-pointer">{t('backups.history')}</summary>
                <ul className="mt-2 space-y-1">
                  {b.recent.slice(0, 10).map((r) => (
                    <li key={r.id} className="flex flex-wrap gap-2">
                      <span className="tabular-nums text-neutral-500">{formatWhen(r.startedAt)}</span>
                      <span>{t(`backups.kinds.${r.kind}`, r.kind)}</span>
                      <span className={r.ok ? 'text-emerald-400' : 'text-red-400'}>{r.ok ? t('backups.ok') : t('backups.failed')}</span>
                      {r.bytes ? <span>{formatBytes(r.bytes)}</span> : null}
                      {!r.ok && r.detail && <span className="text-neutral-500">{r.detail}</span>}
                    </li>
                  ))}
                </ul>
              </details>
            )}
            <p className="text-xs text-neutral-500">{t('backups.manual')} <code className="text-neutral-300">docker compose exec backup backup.sh</code></p>
          </>
        )}
      </CardContent>
    </Card>
  )
}
