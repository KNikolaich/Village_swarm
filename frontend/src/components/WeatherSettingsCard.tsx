import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState, type FormEvent } from 'react'
import { toast } from 'sonner'
import { api, call } from '@/api/client'
import { useMe, useWeather } from '@/api/queries'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle, Input } from '@/components/ui/primitives'
import { t } from '@/i18n'
import { formatAgo } from '@/lib/format'

/** Where the weather is for and which Open-Meteo to ask (admins only). */
export function WeatherSettingsCard() {
  const me = useMe()
  const qc = useQueryClient()
  const isAdmin = me.data?.roles.includes('admin') ?? false
  const settings = useQuery({ queryKey: ['settings', 'weather'], queryFn: () => call(api.GET('/api/settings/weather')), enabled: isAdmin })
  const weather = useWeather()
  const [baseUrl, setBaseUrl] = useState('')
  const [latitude, setLatitude] = useState('')
  const [longitude, setLongitude] = useState('')
  const [timezone, setTimezone] = useState('')
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    if (!settings.data) return
    setBaseUrl(settings.data.baseUrl)
    setLatitude(settings.data.latitude?.toString() ?? '')
    setLongitude(settings.data.longitude?.toString() ?? '')
    setTimezone(settings.data.timezone)
  }, [settings.data])

  if (!isAdmin) return null

  const number = (s: string) => Number(s.replace(',', '.'))
  const valid = baseUrl.trim() !== '' && latitude.trim() !== '' && longitude.trim() !== '' && timezone.trim() !== '' && !Number.isNaN(number(latitude)) && !Number.isNaN(number(longitude))

  async function save(e: FormEvent) {
    e.preventDefault()
    setBusy(true)
    try {
      await call(api.PUT('/api/settings/weather', { body: { baseUrl: baseUrl.trim(), latitude: number(latitude), longitude: number(longitude), timezone: timezone.trim() } }))
      toast.success(t('weather.settings.saved'))
      await Promise.all([qc.invalidateQueries({ queryKey: ['weather'] }), qc.invalidateQueries({ queryKey: ['settings', 'weather'] })])
    } catch {
      toast.error(t('weather.settings.rejected'))
    } finally {
      setBusy(false)
    }
  }

  async function refresh() {
    setBusy(true)
    try {
      await call(api.POST('/api/weather/refresh'))
      await qc.invalidateQueries({ queryKey: ['weather'] })
    } catch {
      toast.error(t('errors.generic'))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Card>
      <CardHeader><CardTitle>{t('weather.settings.title')}</CardTitle></CardHeader>
      <CardContent>
        <form onSubmit={save} className="space-y-2">
          <p className="text-xs text-neutral-500">{t('weather.settings.hint')}</p>
          <label className="block space-y-1 text-xs text-neutral-400">
            {t('weather.settings.baseUrl')}
            <Input value={baseUrl} onChange={(e) => setBaseUrl(e.target.value)} placeholder="https://api.open-meteo.com" />
          </label>
          <div className="grid grid-cols-2 gap-2">
            <label className="block space-y-1 text-xs text-neutral-400">
              {t('weather.settings.latitude')}
              <Input inputMode="decimal" value={latitude} onChange={(e) => setLatitude(e.target.value)} placeholder="59.9473" />
            </label>
            <label className="block space-y-1 text-xs text-neutral-400">
              {t('weather.settings.longitude')}
              <Input inputMode="decimal" value={longitude} onChange={(e) => setLongitude(e.target.value)} placeholder="31.03845" />
            </label>
          </div>
          <label className="block space-y-1 text-xs text-neutral-400">
            {t('weather.settings.timezone')}
            <Input value={timezone} onChange={(e) => setTimezone(e.target.value)} placeholder="Europe/Moscow" />
          </label>
          <div className="flex flex-wrap items-center gap-2 pt-1">
            <Button type="submit" disabled={!valid || busy}>{t('weather.settings.save')}</Button>
            <Button type="button" variant="outline" disabled={busy || !weather.data?.configured} onClick={() => void refresh()}>{t('weather.settings.refresh')}</Button>
            {weather.data?.fetchedAt && <span className="text-xs text-neutral-500">{t('weather.settings.updated')}: {formatAgo(weather.data.fetchedAt)}</span>}
          </div>
          {weather.data?.error && <p className="text-xs text-amber-400">{t('weather.error').replace('{error}', weather.data.error)}</p>}
        </form>
      </CardContent>
    </Card>
  )
}
