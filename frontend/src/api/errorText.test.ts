import { describe, expect, it, vi } from 'vitest'

// client.ts reads window.location at import: the test runs in node, so give it a minimal one.
vi.stubGlobal('window', { location: { origin: 'http://localhost' }, dispatchEvent: () => true })
const { ApiError, call, errorText, failureText } = await import('./client')

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

describe('failureText', () => {
  it('takes the server reason out of a raw result, validation messages first', () => {
    expect(failureText({ error: { title: 'Только дома' } }, 'generic')).toBe('Только дома')
    expect(failureText({ error: { title: 'x', errors: { host: ['Нужен адрес'] } } }, 'generic')).toBe('Нужен адрес')
  })

  it('falls back when the body says nothing', () => {
    expect(failureText({ error: undefined }, 'generic')).toBe('generic')
    expect(failureText({ error: {} }, 'generic')).toBe('generic')
  })
})
