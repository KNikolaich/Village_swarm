import en from './en.json'
import ru from './ru.json'

type Dict = { [key: string]: string | Dict }
export type Language = 'ru' | 'en'

const dictionaries: Record<Language, Dict> = { ru: ru as Dict, en: en as Dict }
let current: Language = 'ru'

/** The language of the whole hive (spec 8.1). Set once at start from the server; changing it reloads the page. */
export function setLanguage(language: Language) {
  current = language
}

export function getLanguage(): Language {
  return current
}

/** `t('nav.events')`. A missing English string falls back to Russian, a missing key to the fallback or the key. */
export function t(key: string, fallback?: string): string {
  // A leaf key may itself contain dots ("secrets.names.telegram.token"): try the longest joined segment at every level.
  const find = (node: string | Dict | undefined, parts: string[]): string | undefined => {
    if (typeof node === 'string') return parts.length === 0 ? node : undefined
    if (node === undefined || parts.length === 0) return undefined
    for (let n = 1; n <= parts.length; n++) {
      const hit = find(node[parts.slice(0, n).join('.')], parts.slice(n))
      if (hit !== undefined) return hit
    }
    return undefined
  }
  const lookup = (dict: Dict) => find(dict, key.split('.'))
  return lookup(dictionaries[current]) ?? lookup(ru as Dict) ?? fallback ?? key
}

export const eventTypeLabel = (type: string) => t(`eventTypes.${type}`, type)
export const severityLabel = (severity: string) => t(`severity.${severity}`, severity)
export const statusLabel = (status: string) => t(`status.${status}`, status)
/** temperature_3 of a third probe reads "Температура 3" without a key of its own. */
export const metricLabel = (metric: string) => {
  const probe = /^temperature_(\d+)$/.exec(metric)
  return t(`metrics.${metric}`, probe ? `${t('metrics.temperature')} ${probe[1]}` : metric)
}
