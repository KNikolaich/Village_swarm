import { describe, expect, it } from 'vitest'
import { addLiveReading } from './liveSeries'

const at = (s: string) => `2026-10-20T12:${s}.000Z`

describe('addLiveReading', () => {
  const buckets = [
    { ts: at('00:00'), avg: 100, min: 99, max: 101 },
    { ts: at('00:10'), avg: 102, min: 101, max: 103 },
  ]

  it('merges a reading inside the newest bucket into its mean and extremes', () => {
    const out = addLiveReading(buckets, at('00:14'), 110, 'a')
    expect(out).toHaveLength(2)
    expect(out[1]).toEqual({ ts: at('00:10'), avg: 106, min: 101, max: 110 })
  })

  it('starts a new bucket on the server grid after the newest one', () => {
    const out = addLiveReading(buckets, at('00:27'), 95, 'b')
    expect(out.at(-1)).toEqual({ ts: at('00:20'), avg: 95, min: 95, max: 95 })
  })

  it('ignores a reading older than the chart and starts an empty chart', () => {
    expect(addLiveReading(buckets, at('00:00'), 1, 'c')).toHaveLength(2)
    expect(addLiveReading([], at('00:00'), 7, 'd')).toEqual([{ ts: at('00:00'), avg: 7, min: 7, max: 7 }])
  })
})
