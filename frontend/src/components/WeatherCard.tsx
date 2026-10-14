import { Droplets, Sunrise, Sunset, Wind } from 'lucide-react'
import { Link } from 'react-router'
import type { WeatherDto } from '@/api/client'
import { useMe, useWeather } from '@/api/queries'
import { Badge, Card, CardContent, CardHeader, CardTitle, Skeleton } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { formatAgo, formatFixed, formatHm, formatWeekdayShort } from '@/lib/format'
import { compass, formatWind, weatherIcon, weatherLabel } from '@/lib/weather'

const degrees = (v: number) => `${formatFixed(Math.round(v), 0)}°`

/** The forecast in short (spec 8.2 screen 1): now, the next hours, the next days, sun times. */
export function WeatherCard() {
  const weather = useWeather()
  const me = useMe()
  const isAdmin = me.data?.roles.includes('admin') ?? false

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t('weather.title')}</CardTitle>
        {weather.data?.fetchedAt && !weather.data.stale && <span className="text-xs text-neutral-500">{formatAgo(weather.data.fetchedAt)}</span>}
        {weather.data?.stale && <Badge tone="alarm">{t('weather.stale')}</Badge>}
      </CardHeader>
      <CardContent className="space-y-4">
        {weather.isLoading && <Skeleton className="h-28" />}
        {weather.data && <Body weather={weather.data} isAdmin={isAdmin} />}
      </CardContent>
    </Card>
  )
}

function Body({ weather, isAdmin }: { weather: WeatherDto; isAdmin: boolean }) {
  if (!weather.configured)
    return (
      <p className="text-sm text-neutral-400">
        {isAdmin ? t('weather.noPlaceAdmin') : t('weather.noPlace')}{' '}
        {isAdmin && <Link to="/settings" className="text-amber-400">{t('weather.openSettings')}</Link>}
      </p>
    )
  if (!weather.current)
    return <p className="text-sm text-neutral-400">{weather.error ? t('weather.error').replace('{error}', weather.error) : t('weather.noData')}</p>

  const now = weather.current
  const Icon = weatherIcon(now.weatherCode)
  // Dates in the answer are the forecast place's local ones: the current time's date is "today" there.
  const today = now.time.slice(0, 10)
  const days = weather.daily.filter((d) => d.date >= today).slice(0, 5)
  const sun = days[0]
  const nowMs = new Date(now.time).getTime()
  const start = Math.max(0, weather.hourly.findIndex((h) => new Date(h.time).getTime() >= nowMs))
  // Every third hour from now on: a day ahead in eight tiles.
  const hours = weather.hourly.slice(start).filter((_, i) => i % 3 === 0).slice(0, 8)

  return (
    <>
      {weather.error && <p className="text-xs text-amber-400">{t('weather.error').replace('{error}', weather.error)}</p>}
      <div className="flex items-center gap-4">
        <Icon className="size-12 shrink-0 text-amber-300" strokeWidth={1.5} />
        <div className="min-w-0">
          <p className="text-3xl font-semibold tabular-nums">{degrees(now.temperature)}</p>
          <p className="truncate text-sm text-neutral-300">
            {weatherLabel(now.weatherCode)}
            {now.apparent != null && <span className="text-neutral-500"> · {t('weather.feelsLike').replace('{n}', degrees(now.apparent))}</span>}
          </p>
        </div>
        <div className="ml-auto space-y-1 text-right text-xs text-neutral-400">
          {now.windSpeed != null && (
            <p className="flex items-center justify-end gap-1"><Wind className="size-3.5" />{formatWind(now.windSpeed)}{now.windDirection != null && ` ${compass(now.windDirection)}`}</p>
          )}
          {now.humidity != null && <p className="flex items-center justify-end gap-1"><Droplets className="size-3.5" />{Math.round(now.humidity)}%</p>}
          {sun?.sunrise && sun.sunset && (
            <p className="flex items-center justify-end gap-2">
              <span className="flex items-center gap-0.5"><Sunrise className="size-3.5" />{formatHm(sun.sunrise)}</span>
              <span className="flex items-center gap-0.5"><Sunset className="size-3.5" />{formatHm(sun.sunset)}</span>
            </p>
          )}
        </div>
      </div>

      <div className="grid grid-cols-4 gap-2 sm:grid-cols-8" aria-label={t('weather.hours')}>
        {hours.map((h) => {
          const HourIcon = weatherIcon(h.weatherCode)
          return (
            <div key={h.time} className="rounded-lg bg-neutral-800/50 p-2 text-center">
              <p className="text-xs text-neutral-500">{formatHm(h.time)}</p>
              <HourIcon className="mx-auto my-1 size-5 text-neutral-300" strokeWidth={1.5} />
              <p className="text-sm tabular-nums">{degrees(h.temperature)}</p>
              <p className="h-3 text-[10px] text-sky-300">{(h.precipitationProbability ?? 0) >= 30 ? `${h.precipitationProbability}%` : ''}</p>
            </div>
          )
        })}
      </div>

      <ul className="divide-y divide-neutral-800">
        {days.map((d) => {
          const DayIcon = weatherIcon(d.weatherCode)
          return (
            <li key={d.date} className="flex items-center gap-3 py-1.5 text-sm">
              <span className="w-16 shrink-0 text-neutral-400">{d.date === today ? t('weather.today') : formatWeekdayShort(d.date)}</span>
              <DayIcon className="size-5 shrink-0 text-neutral-300" strokeWidth={1.5} />
              <span className="min-w-0 flex-1 truncate text-xs text-neutral-500">{weatherLabel(d.weatherCode)}</span>
              {d.precipitationSum >= 0.1 && <span className="text-xs tabular-nums text-sky-300">{formatFixed(d.precipitationSum, 1)} {t('weather.mm')}</span>}
              <span className="w-20 text-right tabular-nums">{degrees(d.temperatureMin)} … {degrees(d.temperatureMax)}</span>
            </li>
          )
        })}
      </ul>
    </>
  )
}
