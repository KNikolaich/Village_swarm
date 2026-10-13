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

export function formatValue(metric: string, value: number): string {
  const unit = metric === 'temperature' || metric === 'cpu_temperature' ? '°' : metric === 'humidity' || metric === 'disk_used_pct' ? '%' : ''
  return `${value.toLocaleString(locale(), { maximumFractionDigits: 1 })}${unit}`
}
