import { describe, expect, it, vi } from 'vitest'
import { preparePhoneSetup } from '@/lib/phoneSetupFlow'
import { portalLink } from '@/lib/setupLink'

describe('the QR for a phone does not wait for USB', () => {
  it('makes the monitor first, then issues its token on demand', async () => {
    const order: string[] = []
    const token = await preparePhoneSetup({
      ensureMonitor: async () => { order.push('monitor') },
      issueToken: async () => { order.push('token'); return 'abcd-efgh-jkmn-pqrs-tuvw' },
    })
    expect(order).toEqual(['monitor', 'token'])
    // the code carries what the panel's form needs
    const link = new URL(portalLink({ url: 'https://h.example', token, ssid: 'Дача', pass: 'p', intervalMin: 10 }))
    expect(link.searchParams.get('token')).toBe('abcd-efgh-jkmn-pqrs-tuvw')
    expect(link.searchParams.get('url')).toBe('https://h.example')
  })

  it('issues no token (so the old one keeps working) when the monitor could not be made', async () => {
    const issue = vi.fn(async () => 'never')
    await expect(preparePhoneSetup({ ensureMonitor: async () => { throw new Error('id taken') }, issueToken: issue })).rejects.toThrow('id taken')
    expect(issue).not.toHaveBeenCalled()
  })
})
