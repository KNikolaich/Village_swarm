import { describe, expect, it } from 'vitest'
import { loadSidebarCollapsed, saveSidebarCollapsed, sidebarCollapsed } from './sidebar'

describe('sidebar', () => {
  it('keeps the wide-screen choice and narrows itself on a tablet unless opened there', () => {
    expect(sidebarCollapsed(true, false, false)).toBe(false)
    expect(sidebarCollapsed(true, true, false)).toBe(true)
    expect(sidebarCollapsed(false, false, false)).toBe(true)
    expect(sidebarCollapsed(false, true, true)).toBe(false)
  })

  it('remembers the choice per browser and survives closed storage', () => {
    const store = new Map<string, string>()
    const storage = { getItem: (k: string) => store.get(k) ?? null, setItem: (k: string, v: string) => void store.set(k, v) }
    expect(loadSidebarCollapsed(storage)).toBe(false)
    saveSidebarCollapsed(true, storage)
    expect(loadSidebarCollapsed(storage)).toBe(true)
    const closed = {
      getItem: () => {
        throw new Error('closed')
      },
      setItem: () => {
        throw new Error('closed')
      },
    }
    expect(loadSidebarCollapsed(closed)).toBe(false)
    expect(() => saveSidebarCollapsed(true, closed)).not.toThrow()
  })
})
