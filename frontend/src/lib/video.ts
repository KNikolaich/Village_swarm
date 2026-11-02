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

export type PtzRequest = Schemas['PtzRequest']

/** While an arrow is held the move is repeated this often; the hive stops the camera 1 s after the last one. */
export const ptzRepeatMs = 400

/** Speed of the arrows and the zoom buttons: half, slow enough to aim with a long zoom. */
export const ptzSpeed = 0.5

export type PtzDirection = 'up' | 'down' | 'left' | 'right' | 'zoomIn' | 'zoomOut'

/** The move an arrow or a zoom button sends (positive pan: right, tilt: up, zoom: in, as the contract says). */
export function ptzMove(direction: PtzDirection, speed = ptzSpeed): PtzRequest {
  switch (direction) {
    case 'up': return { op: 'move', tilt: speed }
    case 'down': return { op: 'move', tilt: -speed }
    case 'left': return { op: 'move', pan: -speed }
    case 'right': return { op: 'move', pan: speed }
    case 'zoomIn': return { op: 'move', zoom: speed }
    case 'zoomOut': return { op: 'move', zoom: -speed }
  }
}

type PtzPreset = Schemas['PtzPresetDto']

/** At most this many presets are offered and toured: cameras often report all 255 slots, set or not. */
export const tourPresetCount = 16

/** The presets the widget offers: the camera's first 16, or 1..16 when it lists none (Dahua indexes, most ONVIF tokens). */
export function tourPresets(presets: readonly PtzPreset[]): PtzPreset[] {
  if (presets.length > 0) return presets.slice(0, tourPresetCount)
  return Array.from({ length: tourPresetCount }, (_, i) => ({ token: String(i + 1), name: String(i + 1) }))
}

/** The pauses offered for a tour, in seconds; 3 by default. */
export const tourPauses = [2, 3, 5, 10, 15, 30] as const
export const defaultTourPause = 3

/** Time a camera is given to reach a preset before the pause starts counting. */
export const ptzTravelMs = 2000

/** From one preset of a tour to the next: the travel, then the pause to look. */
export function tourStepMs(pauseS: number): number {
  return ptzTravelMs + pauseS * 1000
}

/** The next preset of a tour, round and round. */
export function nextTourIndex(index: number, count: number): number {
  return count > 0 ? (index + 1) % count : 0
}

const tourPauseKey = 'overview.ptzTourPause'

export function loadTourPause(storage: Pick<Storage, 'getItem'> | undefined = globalThis.localStorage): number {
  try {
    const value = Number(storage?.getItem(tourPauseKey))
    return (tourPauses as readonly number[]).includes(value) ? value : defaultTourPause
  } catch {
    return defaultTourPause
  }
}

export function saveTourPause(pauseS: number, storage: Pick<Storage, 'setItem'> | undefined = globalThis.localStorage) {
  try {
    storage?.setItem(tourPauseKey, String(pauseS))
  } catch {
    // private window or storage full: just not remembered
  }
}

/** No video data for this long while playing: the connection is taken as lost and the stream stopped. */
export const stallAfterMs = 8000

/** Whether a live stream stalled: it has started (data came once) and nothing came since for <see stallAfterMs>. */
export function isStalled(lastDataAt: number | null, now: number): boolean {
  return lastDataAt !== null && now - lastDataAt >= stallAfterMs
}
