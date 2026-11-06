import { describe, expect, it } from 'vitest'
import { brandLabel, brandVersion } from './version'

describe('label beside the brand', () => {
  it('is never empty and tells an old page', () => {
    expect(brandLabel('466ea82', '466ea82')).toEqual({ text: '466ea82', stale: false })
    expect(brandLabel('466ea82', 'a179481')).toEqual({ text: '466ea82', stale: true })
    expect(brandLabel(null, 'a179481')).toEqual({ text: 'a179481', stale: false })
    expect(brandLabel(undefined, undefined)).toEqual({ text: 'dev', stale: false })
  })
})

describe('version beside the brand', () => {
  it('is the version as Settings shows it, cut when too long', () => {
    expect(brandVersion('a179481')).toBe('a179481')
    expect(brandVersion(' a179481 ')).toBe('a179481')
    expect(brandVersion('v0.3.0-14-ga179481-dirty')).toBe('v0.3.0-14-ga1…')
    expect(brandVersion('')).toBeNull()
    expect(brandVersion(undefined)).toBeNull()
  })
})
