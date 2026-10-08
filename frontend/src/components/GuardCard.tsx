import { ShieldCheck, ShieldOff } from 'lucide-react'
import { toast } from 'sonner'
import { ApiError } from '@/api/client'
import { useArmed, useMe, useSetArmed } from '@/api/queries'
import { Button } from '@/components/ui/button'
import { Card, CardContent, Skeleton } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { formatAgo } from '@/lib/format'
import { cn } from '@/lib/utils'

/** The big guard switch on the overview (spec 8.2 screen 1). Disarming asks for confirmation. */
export function GuardCard({ className }: { className?: string }) {
  const armed = useArmed()
  const set = useSetArmed()
  const me = useMe()
  const canControl = me.data?.roles.some((r) => r === 'admin' || r === 'member') ?? false
  const on = armed.data?.armed ?? false

  function toggle() {
    if (on && !window.confirm(t('overview.disarmConfirm'))) return
    set.mutate(!on, {
      onError: (e) => toast.error(e instanceof ApiError && e.status === 403 ? t('errors.forbidden') : t('errors.generic')),
    })
  }

  return (
    <Card className={cn(on ? 'border-emerald-900/70 bg-emerald-950/30' : 'border-neutral-800', className)}>
      <CardContent className="flex items-center justify-between gap-3">
        <div className="min-w-0">
          <p className="text-sm text-neutral-400">{t('overview.guard')}</p>
          {armed.isLoading ? <Skeleton className="mt-2 h-7 w-28" /> : (
            <p className={cn('mt-1 flex items-center gap-2 text-2xl font-semibold', on ? 'text-emerald-400' : 'text-neutral-300')}>
              {on ? <ShieldCheck className="size-6" /> : <ShieldOff className="size-6" />}
              {on ? t('overview.armed') : t('overview.disarmed')}
            </p>
          )}
          {armed.data && (
            <p className="truncate text-xs text-neutral-500">
              {armed.data.cameras} {t('overview.cameras')} · {armed.data.updatedBy ?? '—'} · {formatAgo(armed.data.updatedAt)}
            </p>
          )}
        </div>
        {canControl && (
          <Button variant={on ? 'secondary' : 'default'} size="lg" disabled={set.isPending || armed.isLoading} onClick={toggle}>
            {on ? t('overview.disarm') : t('overview.arm')}
          </Button>
        )}
      </CardContent>
    </Card>
  )
}
