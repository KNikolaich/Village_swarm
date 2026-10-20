import type { DeviceDto, Schemas } from '@/api/client'

type Build = Schemas['FirmwareBuildDto']

/** A deep sleeper between wake-ups: an order sent now would not reach it. */
export function asleep(d: DeviceDto, now = Date.now()): boolean {
  if (!d.sleepS || d.sleepMode !== 'deep' || d.status !== 'online' || !d.lastSeenAt) return false
  return now - new Date(d.lastSeenAt).getTime() > 90_000
}

/** The newest build for this hornet's role and board that is not the version it runs (null: up to date). Same rule as the hive. */
export function newerBuild(d: DeviceDto, builds: readonly Build[]): Build | null {
  return builds
    .filter((b) => b.role === d.type && b.board === d.hw && !!b.version && b.version !== d.fw)
    .sort((a, b) => new Date(b.built).getTime() - new Date(a.built).getTime())[0] ?? null
}

/** An ESP8266 copies the new image over the old one at restart and cannot go back to it. */
export const noRollback = (b: Build) => b.chipFamily.toUpperCase().startsWith('ESP8266')

export interface Candidate {
  device: DeviceDto
  build: Build
  noRollback: boolean
  /** Why it cannot be picked now: offline, asleep (empty: it can). */
  blocker: '' | 'offline' | 'asleep'
}

/** Hornets with a newer build: those that can roll back first (a bad build shows itself there), then by name. */
export function candidates(devices: readonly DeviceDto[], builds: readonly Build[], now = Date.now()): Candidate[] {
  return devices
    .filter((d) => d.driver === 'hornet' && d.type !== 'hive' && d.status !== 'disabled')
    .flatMap((device) => {
      const build = newerBuild(device, builds)
      if (!build) return []
      const blocker: Candidate['blocker'] = device.status !== 'online' ? 'offline' : asleep(device, now) ? 'asleep' : ''
      return [{ device, build, noRollback: noRollback(build), blocker }]
    })
    .sort((a, b) => Number(a.noRollback) - Number(b.noRollback) || a.device.name.localeCompare(b.device.name, 'ru'))
}

/** Ticked when the panel opens: every hornet that can take it now. ESP8266 ones are left to a deliberate choice. */
export const preselected = (list: readonly Candidate[]) => list.filter((c) => !c.blocker && !c.noRollback).map((c) => c.device.deviceId)

/** What goes to the hive: the ticked hornets that can take it, ESP8266 ones only when confirmed. */
export function toSend(list: readonly Candidate[], chosen: readonly string[], confirmNoRollback: boolean): string[] {
  return list
    .filter((c) => chosen.includes(c.device.deviceId) && !c.blocker && (!c.noRollback || confirmNoRollback))
    .map((c) => c.device.deviceId)
}
