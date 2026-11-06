import { useQuery } from '@tanstack/react-query'
import { api, call } from '@/api/client'
import { t } from '@/i18n'
import { cn } from '@/lib/utils'

/**
 * Where this node stands in the home + city pair (spec 11.3), shown only when it is not the plain active node: the city
 * observer reads a copy, and the city that has taken over the alarms says home is gone. Nothing at all for a single node.
 */
export function ClusterBanner() {
  const cluster = useQuery({ queryKey: ['cluster'], queryFn: () => call(api.GET('/api/cluster')), refetchInterval: 15_000, retry: false })
  const c = cluster.data
  if (!c?.enabled) return null

  const homeDown = c.home && c.home.phase !== 'Up'
  const minutes = c.home ? Math.floor(c.home.silentSeconds / 60) : 0
  if (c.role === 'Active' && !homeDown) return null

  const standby = c.role === 'Standby'
  const text = standby
    ? t('cluster.standby').replace('{m}', String(minutes))
    : homeDown
      ? t('cluster.homeDown').replace('{m}', String(minutes))
      : t('cluster.observer')
  const synced = c.catalogAt
    ? ` ${t('cluster.catalog').replace('{time}', new Date(c.catalogAt).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' }))}`
    : ` ${t('cluster.noCatalog')}`
  return (
    <div className={cn('border-b px-4 py-2 text-center text-sm', standby || homeDown ? 'border-amber-900/60 bg-amber-950/30 text-amber-300' : 'border-neutral-800 bg-neutral-900 text-neutral-400')}>
      {text}{!standby && !homeDown && synced}
    </div>
  )
}
