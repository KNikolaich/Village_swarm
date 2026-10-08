// Dates are shown in the browser time zone, which for this house is the house time zone.

const time = new Intl.DateTimeFormat('ru-RU', { hour: '2-digit', minute: '2-digit', second: '2-digit' })
const dayMonth = new Intl.DateTimeFormat('ru-RU', { day: 'numeric', month: 'long' })
const full = new Intl.DateTimeFormat('ru-RU', { day: 'numeric', month: 'long', hour: '2-digit', minute: '2-digit' })
const monthYear = new Intl.DateTimeFormat('ru-RU', { month: 'long', year: 'numeric' })

export const formatTime = (iso: string) => time.format(new Date(iso))
export const formatDayMonth = (d: Date) => dayMonth.format(d)
export const formatMonthYear = (d: Date) => {
  const s = monthYear.format(d).replace(' г.', '')
  return s.charAt(0).toUpperCase() + s.slice(1)
}

/** "14:03:12" today, "вчера 14:03", otherwise "3 октября, 14:03". */
export function formatWhen(iso: string, now = new Date()): string {
  const d = new Date(iso)
  const days = Math.round((startOfDay(now).getTime() - startOfDay(d).getTime()) / 86_400_000)
  if (days === 0) return time.format(d)
  if (days === 1) return `вчера ${d.toTimeString().slice(0, 5)}`
  return full.format(d)
}

/** "только что", "5 мин назад", "2 ч назад", then the date. */
export function formatAgo(iso: string | null | undefined, now = new Date()): string {
  if (!iso) return '—'
  const s = Math.max(0, Math.round((now.getTime() - new Date(iso).getTime()) / 1000))
  if (s < 60) return 'только что'
  if (s < 3600) return `${Math.floor(s / 60)} мин назад`
  if (s < 86_400) return `${Math.floor(s / 3600)} ч назад`
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
  const unit = metric === 'temperature' ? '°' : metric === 'humidity' ? '%' : ''
  return `${value.toLocaleString('ru-RU', { maximumFractionDigits: 1 })}${unit}`
}
