import { ShieldCheck, ShieldEllipsis, ShieldOff } from 'lucide-react'
import { toast } from 'sonner'
import { ApiError } from '@/api/client'
import { type GuardMode, useArmed, useMe, useSetArmed } from '@/api/queries'
import { Card, CardContent, Skeleton } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { formatAgo } from '@/lib/format'
import { cn } from '@/lib/utils'

// Each mode keeps its colour on the button, the state line and the tile: disarmed green, armed red, arriving orange.
const modes = {
  disarmed: {
    state: 'overview.disarmed', action: 'overview.disarm', icon: ShieldOff, text: 'text-emerald-400', card: 'border-emerald-900/60 bg-emerald-950/20',
    on: 'bg-emerald-500 text-neutral-950', off: 'border-emerald-800/70 text-emerald-300 hover:bg-emerald-950/60',
  },
  armed: {
    state: 'overview.armed', action: 'overview.arm', icon: ShieldCheck, text: 'text-red-400', card: 'border-red-900/70 bg-red-950/30',
    on: 'bg-red-500 text-white', off: 'border-red-900/80 text-red-300 hover:bg-red-950/60',
  },
  arriving: {
    state: 'overview.arriving', action: 'overview.arrive', icon: ShieldEllipsis, text: 'text-orange-400', card: 'border-orange-900/70 bg-orange-950/30',
    on: 'bg-orange-500 text-neutral-950', off: 'border-orange-900/80 text-orange-300 hover:bg-orange-950/60',
  },
} as const satisfies Record<GuardMode, object>

const order: GuardMode[] = ['disarmed', 'armed', 'arriving']

/** The guard tile on the overview (spec 8.2 screen 1): three modes, each sent to every camera. Disarming asks for confirmation. */
export function GuardCard({ className }: { className?: string }) {
  const armed = useArmed()
  const set = useSetArmed()
  const me = useMe()
  const canControl = me.data?.roles.some((r) => r === 'admin' || r === 'member') ?? false
  const current: GuardMode = (armed.data?.mode as GuardMode | undefined) ?? (armed.data?.armed ? 'armed' : 'disarmed')
  const view = modes[current] ?? modes.disarmed
  const Icon = view.icon

  function choose(mode: GuardMode) {
    if (mode === current) return
    if (mode === 'disarmed' && !window.confirm(t('overview.disarmConfirm'))) return
    set.mutate(mode, {
      onError: (e) => toast.error(e instanceof ApiError && e.status === 403 ? t('errors.forbidden') : t('errors.generic')),
    })
  }

  return (
    <Card className={cn(armed.data && view.card, className)}>
      <CardContent>
        <p className="text-sm text-neutral-400">{t('overview.guard')}</p>
        {armed.isLoading ? <Skeleton className="mt-2 h-7 w-28" /> : (
          <p className={cn('mt-1 flex items-center gap-2 text-2xl font-semibold', view.text)}>
            <Icon className="size-6 shrink-0" />
            <span className="truncate">{t(view.state)}</span>
          </p>
        )}
        {armed.data && (
          <p className="truncate text-xs text-neutral-500">
            {armed.data.cameras} {t('overview.cameras')} · {armed.data.updatedBy ?? '—'} · {formatAgo(armed.data.updatedAt)}
          </p>
        )}
        {canControl && (
          <div className="mt-3 grid grid-cols-3 gap-1.5" role="group" aria-label={t('overview.guard')}>
            {order.map((mode) => (
              <button
                key={mode}
                type="button"
                aria-pressed={mode === current}
                disabled={set.isPending || armed.isLoading}
                onClick={() => choose(mode)}
                className={cn(
                  'h-8 truncate rounded-lg px-1 text-xs font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-amber-400/60 disabled:opacity-50',
                  mode === current ? modes[mode].on : cn('border', modes[mode].off),
                )}
              >
                {t(modes[mode].action)}
              </button>
            ))}
          </div>
        )}
      </CardContent>
    </Card>
  )
}
