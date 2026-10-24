import { describe, expect, it } from 'vitest'
import type { EventDto } from '@/api/client'
import { alarmCamera, codecList, codecName, defaultChoice, groupCameras, isCodecMismatch, kbps, loadChoice, saveChoice, shouldPlay, type VideoCamera } from './video'

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
