import { t } from '@/i18n'

/** Chart windows offered on the pages, in hours. */
export const ranges = [6, 24, 168, 720, 8760]

export function rangeLabel(hours: number): string {
  if (hours >= 8760) return t('device.rangeYear')
  return hours < 24 ? t('device.rangeHours').replace('{n}', String(hours)) : t('device.rangeDays').replace('{n}', String(hours / 24))
}
