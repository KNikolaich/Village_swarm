import { describe, expect, it } from 'vitest'
import { brandVersion } from './version'

describe('version beside the brand', () => {
  it('is the version as Settings shows it, cut when too long', () => {
    expect(brandVersion('a179481')).toBe('a179481')
    expect(brandVersion(' a179481 ')).toBe('a179481')
    expect(brandVersion('v0.3.0-14-ga179481-dirty')).toBe('v0.3.0-14-ga1…')
    expect(brandVersion('')).toBeNull()
    expect(brandVersion(undefined)).toBeNull()
  })
})
