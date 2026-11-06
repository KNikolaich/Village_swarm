import { t } from '@/i18n'

/** What the server accepts, in minutes (Hive.Modules.Devices.Timelapse.Intervals): 5 minutes to 24 hours. */
export const timelapseIntervals = [5, 10, 15, 30, 60, 90, 120, 180, 240, 360, 480, 720, 1440]

/** "каждые 30 мин", "каждый час", "каждые 6 ч", "раз в сутки". */
export function intervalLabel(min: number): string {
  if (min === 60) return t('timelapse.hour')
  if (min === 1440) return t('timelapse.day')
  return min < 120 ? t('timelapse.minutes').replace('{n}', String(min)) : t('timelapse.hours').replace('{n}', String(min / 60))
}
