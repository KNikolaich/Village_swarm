// Dates are shown in the browser time zone, which for this house is the house time zone.
// Formats follow the hive's interface language (i18n), so Russian and English both read naturally.
import { getLanguage, t } from '@/i18n'

const locale = () => (getLanguage() === 'en' ? 'en-US' : 'ru-RU')
const time = () => new Intl.DateTimeFormat(locale(), { hour: '2-digit', minute: '2-digit', second: '2-digit' })
const dayMonth = () => new Intl.DateTimeFormat(locale(), { day: 'numeric', month: 'long' })
const full = () => new Intl.DateTimeFormat(locale(), { day: 'numeric', month: 'long', hour: '2-digit', minute: '2-digit' })
const monthYear = () => new Intl.DateTimeFormat(locale(), { month: 'long', year: 'numeric' })

export const formatTime = (iso: string) => time().format(new Date(iso))
export const formatDayMonth = (d: Date) => dayMonth().format(d)
export const formatMonthYear = (d: Date) => {
  const s = monthYear().format(d).replace(' г.', '')
  return s.charAt(0).toUpperCase() + s.slice(1)
}

/** "14:03:12" today, "вчера 14:03" / "yesterday 14:03", otherwise "3 октября, 14:03". */
export function formatWhen(iso: string, now = new Date()): string {
  const d = new Date(iso)
  const days = Math.round((startOfDay(now).getTime() - startOfDay(d).getTime()) / 86_400_000)
  if (days === 0) return time().format(d)
  if (days === 1) return `${t('time.yesterday')} ${d.toTimeString().slice(0, 5)}`
  return full().format(d)
}

/** A camera that could not take a fresh frame sends an older one with its own time; a minute of slack (as the hive). */
export function takenBefore(photoTs: string, askedTs: string): boolean {
  return new Date(photoTs).getTime() < new Date(askedTs).getTime() - 60_000
}

/** "только что" / "just now", "5 мин назад" / "5 min ago", "2 ч назад" / "2 h ago", then the date. */
export function formatAgo(iso: string | null | undefined, now = new Date()): string {
  if (!iso) return '—'
  const s = Math.max(0, Math.round((now.getTime() - new Date(iso).getTime()) / 1000))
  if (s < 60) return t('time.justNow')
  if (s < 3600) return t('time.minutesAgo').replace('{n}', String(Math.floor(s / 60)))
  if (s < 86_400) return t('time.hoursAgo').replace('{n}', String(Math.floor(s / 3600)))
  return formatWhen(iso, now)
}

/**
 * A span of minutes in the largest fitting units with the right plural form ("3 часа 44 минуты", "2 суток 5 часов", "1 неделя 2 дня"):
 * minutes, hours, days, weeks, months of 30 days; at most two units, the second dropped when it is zero.
 */
export function formatDuration(totalMinutes: number): string {
  const total = Math.max(0, Math.floor(totalMinutes))
  const rules = new Intl.PluralRules(locale())
  const part = (unit: string, n: number) => {
    const key = `duration.${unit}.${rules.select(n)}`
    return t(key, t(`duration.${unit}.other`)).replace('{n}', String(n))
  }
  const units: [string, number][] = [['month', 43_200], ['week', 10_080], ['day', 1_440], ['hour', 60], ['minute', 1]]
  const i = units.findIndex(([, size]) => total >= size)
  const [unit, size] = units[i < 0 ? units.length - 1 : i]
  const first = Math.floor(total / size)
  const rest = i >= 0 && i < units.length - 1 ? Math.floor((total % size) / units[i + 1][1]) : 0
  return rest > 0 ? `${part(unit, first)} ${part(units[i + 1][0], rest)}` : part(unit, first)
}

export const startOfDay = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate())

/** Local calendar date as YYYY-MM-DD (what the API's `date` parameter expects). */
export function isoDate(d: Date): string {
  const p = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`
}

export function parseIsoDate(s: string): Date {
  const [y, m, d] = s.split('-').map(Number)
  return new Date(y, m - 1, d)
}

export const metricUnit = (metric: string) =>
  metric.startsWith('temperature') || metric === 'cpu_temperature' ? '°'
    : metric === 'humidity' || metric === 'disk_used_pct' || metric === 'cpu_load_pct' || metric === 'mem_used_pct' ? '%'
      : metric === 'battery_voltage' ? ' ' + t('units.volt')
        : metric === 'pressure' ? ' ' + t('units.hpa')
          : metric.startsWith('mem_') && metric.endsWith('_mb') ? ' ' + t('units.mb')
            : metric.startsWith('disk_') && metric.endsWith('_gb') ? ' ' + t('units.gb')
              : ''

export function formatValue(metric: string, value: number): string {
  // Battery voltage needs hundredths: a 12 V battery's whole charge range is about 1.5 V.
  const digits = metric === 'battery_voltage' ? 2 : 1
  return `${value.toLocaleString(locale(), { maximumFractionDigits: digits })}${metricUnit(metric)}`
}

/** A number with a fixed count of decimals in the interface language, for axis labels. */
export const formatFixed = (value: number, decimals: number) =>
  value.toLocaleString(locale(), { minimumFractionDigits: decimals, maximumFractionDigits: decimals })

/** "14:00" for marks inside a day; "12 окт" for midnights and for day-sized steps. */
export function formatTick(d: Date, stepMs: number): string {
  const midnight = d.getHours() === 0 && d.getMinutes() === 0
  if (stepMs >= 86_400_000 || midnight)
    return new Intl.DateTimeFormat(locale(), { day: 'numeric', month: 'short' }).format(d).replace('.', '')
  return new Intl.DateTimeFormat(locale(), { hour: '2-digit', minute: '2-digit', hour12: false }).format(d)
}

/** "13:00" from a timestamp, in the browser (house) time zone. */
export const formatHm = (iso: string) =>
  new Intl.DateTimeFormat(locale(), { hour: '2-digit', minute: '2-digit', hour12: false }).format(new Date(iso))

/** "Ср" / "Wed" for a calendar date "2026-10-14" (a date has no zone, so it is read as UTC and shown as UTC). */
export const formatWeekdayShort = (date: string) =>
  new Intl.DateTimeFormat(locale(), { weekday: 'short', timeZone: 'UTC' }).format(new Date(`${date}T00:00:00Z`))

/** "12,4 МБ" / "12.4 MB": file sizes for backups. */
export function formatBytes(bytes: number | null | undefined): string {
  if (bytes == null) return '—'
  const units = getLanguage() === 'en' ? ['B', 'KB', 'MB', 'GB'] : ['Б', 'КБ', 'МБ', 'ГБ']
  let value = bytes
  let i = 0
  while (value >= 1024 && i < units.length - 1) {
    value /= 1024
    i++
  }
  return `${value.toLocaleString(locale(), { maximumFractionDigits: value < 10 && i > 0 ? 1 : 0 })} ${units[i]}`
}

/** A value the hive does not know (never reported, or an empty string) is not shown as an empty cell. */
export function isBlank(v: unknown): boolean {
  return v == null || v === '' || (typeof v === 'string' && v.trim() === '')
}

/** The last known value, or a dash when it was never known: the device page always has the row. */
export function orDash(v: string | null | undefined): string {
  return isBlank(v) ? '—' : (v as string)
}
