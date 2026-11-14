// @vitest-environment jsdom
import { describe, expect, it } from 'vitest'
import { ApiError, call, errorText } from './client'

describe('errorText', () => {
  it('shows the reason the server gave', () => {
    expect(errorText(new ApiError(403, 'Этот узел наблюдает за домом', true), 'generic')).toBe('Этот узел наблюдает за домом')
  })

  it('falls back when there is no server reason', () => {
    expect(errorText(new ApiError(500), 'generic')).toBe('generic')
    expect(errorText(new Error('boom'), 'generic')).toBe('generic')
  })

  it('call() keeps the title of a refusal and joins validation messages', async () => {
    const refused = call(Promise.resolve({ error: { title: 'Только дома' }, response: new Response(null, { status: 403 }) }))
    await expect(refused).rejects.toMatchObject({ status: 403, message: 'Только дома', fromServer: true })
    const invalid = call(Promise.resolve({ error: { title: 'One or more validation errors occurred.', errors: { ssid: ['1–32 символа'] } }, response: new Response(null, { status: 400 }) }))
    await expect(invalid).rejects.toMatchObject({ message: '1–32 символа' })
  })
})
