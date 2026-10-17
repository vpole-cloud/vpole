import { afterEach, expect, it, vi } from 'vitest'
import { jsonResponse, strictFetch } from '../test/http'
import { createRealtimeSession, revokeRealtimeSession } from './sessionApi'

afterEach(() => vi.unstubAllGlobals())
it('bootstraps with bearer HTTP and cookies without a tenant header, then deletes using only the cookie', async () => {
  const fixture = strictFetch()
  vi.stubGlobal('fetch', fixture.fetch)
  fixture.fetch.mockImplementation((url, init) => {
    if (String(url) !== 'https://pole.example/api/v1/realtime/session')
      return fixture.unhandled(url, init)
    expect(init?.credentials).toBe('include')
    const headers = new Headers(init?.headers)
    expect(headers.has('X-Pole-Tenant-ID')).toBe(false)
    if (init?.method === 'POST') {
      expect(headers.get('Authorization')).toBe('Bearer current-credential')
      return jsonResponse({ expiresAtUtc: '2026-10-12T21:00:00Z' })
    }
    if (init?.method === 'DELETE') {
      expect(headers.has('Authorization')).toBe(false)
      expect(init.signal).toBeInstanceOf(AbortSignal)
      return Promise.resolve(new Response(null, { status: 204 }))
    }
    return fixture.unhandled(url, init)
  })
  const signal = new AbortController().signal
  expect(await createRealtimeSession('https://pole.example', 'current-credential', signal)).toEqual(
    { expiresAtUtc: '2026-10-12T21:00:00Z' },
  )
  await revokeRealtimeSession('https://pole.example')
  expect(fixture.fetch).toHaveBeenCalledTimes(2)
  fixture.verify()
})
