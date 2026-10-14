import { Cloud, CloudDrizzle, CloudFog, CloudHail, CloudLightning, CloudRain, CloudSnow, CloudSun, Snowflake, Sun, type LucideIcon } from 'lucide-react'
import { t } from '@/i18n'

/** WMO weather code (as Open-Meteo reports it) -> the group it is shown and worded as. */
export function weatherGroup(code: number): string {
  if (code === 0) return 'clear'
  if (code === 1) return 'mostlyClear'
  if (code === 2) return 'partlyCloudy'
  if (code === 3) return 'overcast'
  if (code === 45 || code === 48) return 'fog'
  if (code >= 51 && code <= 55) return 'drizzle'
  if (code === 56 || code === 57 || code === 66 || code === 67) return 'freezingRain'
  if (code >= 61 && code <= 65) return 'rain'
  if (code >= 71 && code <= 75) return 'snow'
  if (code === 77) return 'snow'
  if (code >= 80 && code <= 82) return 'showers'
  if (code === 85 || code === 86) return 'snowShowers'
  if (code === 96 || code === 99) return 'hail'
  if (code === 95) return 'thunderstorm'
  return 'overcast'
}

const icons: Record<string, LucideIcon> = {
  clear: Sun,
  mostlyClear: CloudSun,
  partlyCloudy: CloudSun,
  overcast: Cloud,
  fog: CloudFog,
  drizzle: CloudDrizzle,
  freezingRain: Snowflake,
  rain: CloudRain,
  showers: CloudRain,
  snow: CloudSnow,
  snowShowers: CloudSnow,
  thunderstorm: CloudLightning,
  hail: CloudHail,
}

export const weatherIcon = (code: number): LucideIcon => icons[weatherGroup(code)] ?? Cloud
export const weatherLabel = (code: number) => t(`weather.codes.${weatherGroup(code)}`)

/** 8-point compass word for a wind direction in degrees (where it blows from). */
export function compass(degrees: number): string {
  const names = ['n', 'ne', 'e', 'se', 's', 'sw', 'w', 'nw']
  return t(`weather.compass.${names[Math.round(((degrees % 360) + 360) % 360 / 45) % 8]}`)
}

/** "4 м/с" or "4 m/s". */
export const formatWind = (ms: number) => `${Math.round(ms)} ${t('weather.ms')}`
