import { type DeviceDto } from '@/api/client'

/** A battery hornet between wake-ups: status stays online (clean disconnect), but it has not spoken lately. */
export function asleep(d: DeviceDto): boolean {
  if (!d.sleepS || d.sleepMode !== 'deep' || d.status !== 'online' || !d.lastSeenAt) return false
  return Date.now() - new Date(d.lastSeenAt).getTime() > 90_000
}
