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
  const lookup = (dict: Dict) => {
    let node: string | Dict | undefined = dict
    for (const part of key.split('.')) node = typeof node === 'object' ? node[part] : undefined
    return typeof node === 'string' ? node : undefined
  }
  return lookup(dictionaries[current]) ?? lookup(ru as Dict) ?? fallback ?? key
}

export const eventTypeLabel = (type: string) => t(`eventTypes.${type}`, type)
export const severityLabel = (severity: string) => t(`severity.${severity}`, severity)
export const statusLabel = (status: string) => t(`status.${status}`, status)
export const metricLabel = (metric: string) => t(`metrics.${metric}`, metric)
