import type { DeviceDto } from '@/api/client'

/** A reading older than this is shown dimmed even if the hornet still counts as online. */
export const staleAfterMs = 3 * 3_600_000

/** Whether a hornet's reading has gone quiet: the hornet is offline, or its newest value is old. */
export function isStale(device: Pick<DeviceDto, 'status'> | undefined, ts: string | null | undefined, now = Date.now()): boolean {
  if (device && device.status !== 'online') return true
  return !ts || now - new Date(ts).getTime() > staleAfterMs
}
