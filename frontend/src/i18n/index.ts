import ru from './ru.json'

type Dict = { [key: string]: string | Dict }

/** UI language is Russian (spec 8.1). `t('nav.events')`; unknown keys return the fallback or the key itself. */
export function t(key: string, fallback?: string): string {
  let node: string | Dict | undefined = ru as Dict
  for (const part of key.split('.')) {
    node = typeof node === 'object' ? node[part] : undefined
  }
  return typeof node === 'string' ? node : (fallback ?? key)
}

export const eventTypeLabel = (type: string) => t(`eventTypes.${type}`, type)
export const severityLabel = (severity: string) => t(`severity.${severity}`, severity)
export const statusLabel = (status: string) => t(`status.${status}`, status)
export const metricLabel = (metric: string) => t(`metrics.${metric}`, metric)
