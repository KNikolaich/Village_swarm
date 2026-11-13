import { describe, expect, it } from 'vitest'
import { titleFromManifest } from './hiveTitle'

describe('titleFromManifest', () => {
  it('takes the hive alias from short_name', () => {
    expect(titleFromManifest({ name: 'Улей — Дача', short_name: ' Дача ' })).toBe('Дача')
  })
  it('gives nothing for a manifest without a usable name', () => {
    expect(titleFromManifest({ short_name: '  ' })).toBeNull()
    expect(titleFromManifest({})).toBeNull()
    expect(titleFromManifest(null)).toBeNull()
  })
})
