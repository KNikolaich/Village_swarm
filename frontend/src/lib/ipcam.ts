import { type Schemas } from '@/api/client'
import { getLanguage, t } from '@/i18n'

export type Nvr = Schemas['NvrDto']
export type Probe = Schemas['ProbeResultDto']

/** A channel on the form: whether it goes into the swarm, under which name and group. */
export type Row = {
  channel: number
  take: boolean
  name: string
  zoneId: number | null
  recordWhenDisarmed: boolean
  snapshotSource: string
  snapshotQuality: string
  title?: string | null
  connected?: boolean | null
}

export const snapshotSources = ['auto', 'cgi', 'rtsp']
export const snapshotQualities = ['fast', 'detailed']

export const clock = (iso?: string | null) => (iso ? new Date(iso).toLocaleTimeString(getLanguage() === 'en' ? 'en-GB' : 'ru-RU') : '—')

export const nvrsKey = ['ipcams'] as const

export const errorText = (code?: string | null) => t(`ipcam.errors.${code ?? 'unreachable'}`, t('errors.generic'))

export function problem(err: unknown) {
  return (err as { errors?: Record<string, string[]> } | undefined)?.errors?.request?.[0] ?? t('errors.generic')
}
