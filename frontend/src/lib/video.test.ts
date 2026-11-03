import { describe, expect, it } from 'vitest'
import type { EventDto } from '@/api/client'
import { alarmCamera, codecList, codecName, defaultChoice, driveLink, groupCameras, linkedCamera, isCodecMismatch, isStalled, kbps, loadChoice, loadTourPause, nextTourIndex, ptzMove, ptzRepeatMs, saveChoice, saveTourPause, shouldPlay, tourPresets, tourStepMs, type VideoCamera } from './video'

const camera = (deviceId: string, zone: string | null, name = deviceId) =>
  ({ deviceId, name, zone, zoneId: zone ? 1 : null, nvrId: 1, channel: 1, sub: `${deviceId}-sub`, main: `${deviceId}-main`, status: 'online' }) as VideoCamera

describe('video widget choice', () => {
  it('is none and snapshots by default, and comes back as saved', () => {
    const store = new Map<string, string>()
    const storage = { getItem: (k: string) => store.get(k) ?? null, setItem: (k: string, v: string) => void store.set(k, v) }
    expect(loadChoice(storage)).toEqual(defaultChoice)
    saveChoice({ camera: 'ipcam1-ch2', quality: 'sd' }, storage)
    expect(loadChoice(storage)).toEqual({ camera: 'ipcam1-ch2', quality: 'sd' })
  })

  it('survives closed or broken storage', () => {
    const closed = { getItem: () => { throw new Error('denied') }, setItem: () => { throw new Error('denied') } }
    expect(loadChoice(closed)).toEqual(defaultChoice)
    expect(() => saveChoice({ camera: null, quality: 'hd' }, closed)).not.toThrow()
    expect(loadChoice({ getItem: () => '{"camera":5,"quality":"4k"}' })).toEqual(defaultChoice)
  })
})

describe('camera picker', () => {
  it('groups by zone with cameras without a zone last', () => {
    const groups = groupCameras([camera('a', null), camera('b', 'Улица'), camera('c', 'Дом'), camera('d', 'Улица')], 'Без группы')
    expect(groups.map((g) => [g.zone, g.cameras.map((c) => c.deviceId)])).toEqual([
      ['Улица', ['b', 'd']],
      ['Дом', ['c']],
      ['Без группы', ['a']],
    ])
  })
})

describe('player', () => {
  it('offers only the codecs the browser plays', () => {
    expect(codecList((t) => t.includes('avc1'))).toBe('avc1.640029,avc1.64002A,avc1.640033')
    expect(codecList(() => false)).toBe('')
  })

  it('tells an H.265 camera on a browser without H.265, and names codecs', () => {
    expect(isCodecMismatch('streams: codecs not matched: video:H265')).toBe(true)
    expect(isCodecMismatch('dial tcp: i/o timeout')).toBe(false)
    expect(codecName('video/mp4; codecs="hvc1.1.6.L153.B0"')).toBe('H.265')
    expect(codecName('video/mp4; codecs="avc1.640029,mp4a.40.2"')).toBe('H.264')
  })

  it('stops when the tab is hidden, the widget is off screen or nobody is there', () => {
    const on = { wanted: true, visible: true, inView: true, idle: false }
    expect(shouldPlay(on)).toBe(true)
    expect(shouldPlay({ ...on, visible: false })).toBe(false)
    expect(shouldPlay({ ...on, inView: false })).toBe(false)
    expect(shouldPlay({ ...on, idle: true })).toBe(false)
    expect(shouldPlay({ ...on, wanted: false })).toBe(false)
  })

  it('counts kilobits per second', () => {
    expect(kbps(125_000, 1000)).toBe(1000)
    expect(kbps(1, 0)).toBe(0)
  })
})

describe('alarm button', () => {
  it('offers the camera of the newest unacknowledged alarm, never another', () => {
    const cams = [camera('ipcam1-ch1', 'Улица'), camera('ipcam1-ch2', 'Дом')]
    const ev = (deviceId: string, severity: string, acknowledgedAt: string | null = null) => ({ deviceId, severity, acknowledgedAt }) as EventDto
    expect(alarmCamera([ev('ipcam1-ch2', 'info'), ev('ipcam1-ch1', 'alarm')], cams)?.deviceId).toBe('ipcam1-ch1')
    expect(alarmCamera([ev('ipcam1-ch1', 'alarm', '2026-10-24T10:00:00Z')], cams)).toBeNull()
    expect(alarmCamera([ev('guard-gate1', 'alarm')], cams)).toBeNull()
  })
})

describe('PTZ controls', () => {
  it('sends the contract directions: right, up and zoom in are positive', () => {
    expect(ptzMove('right')).toEqual({ op: 'move', pan: 0.5 })
    expect(ptzMove('left')).toEqual({ op: 'move', pan: -0.5 })
    expect(ptzMove('up', 1)).toEqual({ op: 'move', tilt: 1 })
    expect(ptzMove('down')).toEqual({ op: 'move', tilt: -0.5 })
    expect(ptzMove('zoomIn')).toEqual({ op: 'move', zoom: 0.5 })
    expect(ptzMove('zoomOut')).toEqual({ op: 'move', zoom: -0.5 })
  })

  it('repeats a held move well within the hive\'s 1 s stop', () => {
    expect(ptzRepeatMs).toBeLessThan(1000 / 2)
  })
})

describe('preset tour', () => {
  const presets = Array.from({ length: 255 }, (_, i) => ({ token: String(i + 1), name: String(i + 1) }))

  it('offers 16 presets: the camera\'s first, or 1..16 when it lists none', () => {
    expect(tourPresets(presets).map((p) => p.token)).toEqual(Array.from({ length: 16 }, (_, i) => String(i + 1)))
    expect(tourPresets([{ token: 'a', name: 'Двор' }])).toEqual([{ token: 'a', name: 'Двор' }])
    expect(tourPresets([])).toHaveLength(16)
  })

  it('goes round and round, travel plus pause per step', () => {
    expect(nextTourIndex(0, 16)).toBe(1)
    expect(nextTourIndex(15, 16)).toBe(0)
    expect(nextTourIndex(3, 0)).toBe(0)
    expect(tourStepMs(3)).toBe(5000)
  })

  it('remembers the pause per browser and survives closed storage', () => {
    const store = new Map<string, string>()
    const storage = { getItem: (k: string) => store.get(k) ?? null, setItem: (k: string, v: string) => void store.set(k, v) }
    expect(loadTourPause(storage)).toBe(3)
    saveTourPause(10, storage)
    expect(loadTourPause(storage)).toBe(10)
    expect(loadTourPause({ getItem: () => '7' })).toBe(3)
    expect(loadTourPause({ getItem: () => { throw new Error('denied') } })).toBe(3)
    expect(() => saveTourPause(5, { setItem: () => { throw new Error('denied') } })).not.toThrow()
  })
})

describe('stall', () => {
  it('is a stream that started and then got nothing for 8 s', () => {
    expect(isStalled(null, 100_000)).toBe(false)
    expect(isStalled(10_000, 17_999)).toBe(false)
    expect(isStalled(10_000, 18_000)).toBe(true)
  })
})

describe('drive link', () => {
  it('opens the video on the car and reads it back, nothing else', () => {
    expect(driveLink('car-cam2')).toBe('/?video=car-cam2')
    expect(linkedCamera('?video=car-cam2')).toBe('car-cam2')
    expect(linkedCamera('?video=<script>')).toBeNull()
    expect(linkedCamera('')).toBeNull()
  })
})
