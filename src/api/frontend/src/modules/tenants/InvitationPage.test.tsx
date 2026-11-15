// @vitest-environment jsdom
import { act } from 'react'
import type { Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { createTestApp } from '../../test/TestApp'
import { jsonResponse, strictFetch } from '../../test/http'
import { InvitationPage } from './InvitationPage'

vi.mock('@react-keycloak/web', () => ({
  useKeycloak: () => ({
    initialized: true,
    keycloak: { authenticated: false, login: vi.fn(), logout: vi.fn() },
  }),
}))
vi.mock('./invitationHandoff', () => ({
  initialInvitationToken: 'private-invitation-capability',
  clearInvitationHandoff: vi.fn(),
  saveInvitationHandoff: vi.fn(),
}))
const requests = strictFetch()
let root: Root
let container: HTMLDivElement
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  requests.reset()
  vi.stubGlobal('fetch', requests.fetch)
  vi.stubEnv('VITE_API_BASE_URL', 'api')
  ;({ root, container } = createTestApp())
})
afterEach(async () => {
  await act(async () => root.unmount())
  container.remove()
  requests.verify()
  vi.unstubAllGlobals()
  vi.unstubAllEnvs()
})
it.each(['server', 'network'])(
  'recovers from a temporary %s preview failure using the original in-memory capability',
  async (failure) => {
    let attempts = 0
    requests.fetch.mockImplementation((url, init) => {
      if (String(url) !== 'api/api/v1/tenant-invitations/preview')
        return requests.unhandled(url, init)
      expect(JSON.parse(init!.body as string).token).toBe('private-invitation-capability')
      if (++attempts === 1)
        return failure === 'server'
          ? jsonResponse({ title: 'Temporarily unavailable' }, 500)
          : Promise.reject(new TypeError('Network unavailable'))
      return jsonResponse({
        tenantName: 'Recovered tenant',
        email: 'invited@example.test',
        role: 'Viewer',
        expiresAtUtc: '2030-01-01',
      })
    })
    await act(async () => root.render(<InvitationPage />))
    expect(container.textContent).toContain('Unable to load the invitation')
    expect(container.textContent).not.toContain('has expired')
    const retry = [...container.querySelectorAll('button')].find(
      (button) => button.textContent === 'Retry preview',
    )!
    await act(async () => retry.click())
    expect(container.textContent).toContain('Recovered tenant')
    expect(container.textContent).not.toContain('Unable to load')
    expect(attempts).toBe(2)
  },
)
it('shows permanent unavailability only for the authoritative 410 outcome', async () => {
  requests.fetch.mockResolvedValue(
    new Response(JSON.stringify({ title: 'Invitation unavailable' }), { status: 410 }),
  )
  await act(async () => root.render(<InvitationPage />))
  expect(container.textContent).toContain('has expired, was revoked')
  expect(container.textContent).not.toContain('Retry preview')
})
