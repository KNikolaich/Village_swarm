import { describe, expect, it } from 'vitest'
import { hiveUrl } from './hiveUrl'

describe('hive address for enrollment', () => {
  it('takes a bare host as http and keeps a full address', () => {
    expect(hiveUrl('192.168.10.4')).toBe('http://192.168.10.4')
    expect(hiveUrl(' hive.local:8443/ ')).toBe('http://hive.local:8443')
    expect(hiveUrl('https://hive.example.org/')).toBe('https://hive.example.org')
    expect(hiveUrl('HTTP://192.168.10.4')).toBe('HTTP://192.168.10.4')
    expect(hiveUrl('')).toBe('')
  })
})
