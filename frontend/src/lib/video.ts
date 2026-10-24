import type { EventDto, Schemas } from '@/api/client'

export type VideoCamera = Schemas['VideoCameraDto']

/** What the video widget shows: frames every few seconds, the light stream, or the detailed one. */
export const qualities = ['snapshots', 'sd', 'hd'] as const
export type Quality = (typeof qualities)[number]

/** The widget's choice in this browser: which camera (null: none, nothing is opened) and how. */
export interface VideoChoice {
  camera: string | null
  quality: Quality
}

export const defaultChoice: VideoChoice = { camera: null, quality: 'snapshots' }
const storageKey = 'overview.video'

/** The choice kept in this browser; the default when there is none or storage is closed (private window). */
export function loadChoice(storage: Pick<Storage, 'getItem'> | undefined = globalThis.localStorage): VideoChoice {
  try {
    const raw = storage?.getItem(storageKey)
    if (!raw) return defaultChoice
    const parsed = JSON.parse(raw) as Partial<VideoChoice>
    return {
      camera: typeof parsed.camera === 'string' ? parsed.camera : null,
      quality: qualities.includes(parsed.quality as Quality) ? (parsed.quality as Quality) : 'snapshots',
    }
  } catch {
    return defaultChoice
  }
}

export function saveChoice(choice: VideoChoice, storage: Pick<Storage, 'setItem'> | undefined = globalThis.localStorage) {
  try {
    storage?.setItem(storageKey, JSON.stringify(choice))
  } catch {
    // private window or storage full: just not remembered
  }
}

/** The cameras by zone, in the order the API gives them; cameras without a zone last. */
export function groupCameras(cameras: readonly VideoCamera[], noZone: string): { zone: string; cameras: VideoCamera[] }[] {
  const groups: { zone: string; cameras: VideoCamera[] }[] = []
  for (const c of cameras) {
    const zone = c.zone ?? noZone
    const group = groups.find((g) => g.zone === zone)
    if (group) group.cameras.push(c)
    else groups.push({ zone, cameras: [c] })
  }
  const loose = groups.findIndex((g) => g.zone === noZone)
  if (loose >= 0) groups.push(...groups.splice(loose, 1))
  return groups
}

/** The codecs go2rtc's player offers (video-rtc), those this browser can play through MSE. */
const offered = ['avc1.640029', 'avc1.64002A', 'avc1.640033', 'hvc1.1.6.L153.B0', 'mp4a.40.2', 'mp4a.40.5', 'flac', 'opus']

export function codecList(isSupported: (type: string) => boolean): string {
  return offered.filter((c) => isSupported(`video/mp4; codecs="${c}"`)).join(',')
}

/** go2rtc found no codec of the camera among the browser's: an H.265 camera on a browser without H.265. */
export function isCodecMismatch(error: string): boolean {
  return /codec/i.test(error)
}

/** A readable codec from go2rtc's MSE answer: 'video/mp4; codecs="avc1.640029"' → "H.264", hvc1/hev1 → "H.265". */
export function codecName(mime: string): string {
  if (/hvc1|hev1/i.test(mime)) return 'H.265'
  if (/avc1/i.test(mime)) return 'H.264'
  return mime.replace(/^video\/mp4;\s*codecs=/, '').replace(/"/g, '')
}

/** Whether the stream should be running: asked for, the tab in front, the widget on screen, and someone still there. */
export function shouldPlay(s: { wanted: boolean; visible: boolean; inView: boolean; idle: boolean }): boolean {
  return s.wanted && s.visible && s.inView && !s.idle
}

/** After this long without a touch the stream stops and asks whether to go on. */
export const idleAfterMs = 5 * 60_000

/** The camera of the newest alarm nobody acknowledged yet, if it can be watched: offered, never started by itself. */
export function alarmCamera(events: readonly EventDto[], cameras: readonly VideoCamera[]): VideoCamera | null {
  for (const e of events) {
    if ((e.severity === 'alarm' || e.severity === 'critical') && !e.acknowledgedAt) {
      const camera = cameras.find((c) => c.deviceId === e.deviceId)
      if (camera) return camera
    }
  }
  return null
}

/** Kilobits per second from bytes seen over a span. */
export function kbps(bytes: number, ms: number): number {
  return ms > 0 ? Math.round((bytes * 8) / ms) : 0
}
