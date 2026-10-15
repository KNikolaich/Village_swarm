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

/** "только что" / "just now", "5 мин назад" / "5 min ago", "2 ч назад" / "2 h ago", then the date. */
export function formatAgo(iso: string | null | undefined, now = new Date()): string {
  if (!iso) return '—'
  const s = Math.max(0, Math.round((now.getTime() - new Date(iso).getTime()) / 1000))
  if (s < 60) return t('time.justNow')
  if (s < 3600) return t('time.minutesAgo').replace('{n}', String(Math.floor(s / 60)))
  if (s < 86_400) return t('time.hoursAgo').replace('{n}', String(Math.floor(s / 3600)))
  return formatWhen(iso, now)
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
  metric === 'temperature' || metric === 'cpu_temperature' ? '°'
    : metric === 'humidity' || metric === 'disk_used_pct' ? '%'
      : metric === 'battery_voltage' ? ' ' + t('units.volt')
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
