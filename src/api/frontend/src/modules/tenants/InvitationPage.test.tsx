// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

// @vitest-environment jsdom
import { act } from 'react'
import type { Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { createTestApp } from '../../test/TestApp'
import { deferred, jsonResponse, strictFetch } from '../../test/http'
import { InvitationPage } from './InvitationPage'

const identity = vi.hoisted(() => ({
  authenticated: false,
  subject: 'recipient',
  token: 'bearer',
  tokenParsed: { email: 'invited@example.test', email_verified: true },
  login: vi.fn(),
  logout: vi.fn(),
}))
vi.mock('@react-keycloak/web', () => ({
  useKeycloak: () => ({ initialized: true, keycloak: identity }),
}))
const requests = strictFetch()
let root: Root
let container: HTMLDivElement
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  window.history.replaceState(null, '', '/invitations#token=private-invitation-capability')
  sessionStorage.clear()
  localStorage.clear()
  identity.authenticated = false
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

const preview = (name: string) => ({
  tenantName: name,
  email: 'invited@example.test',
  role: 'Viewer',
  expiresAtUtc: '2030-01-01',
})
async function openNewInvitation() {
  await act(async () => {
    window.history.replaceState(null, '', '/invitations#token=new-private-capability')
    window.dispatchEvent(new HashChangeEvent('hashchange'))
  })
  expect(window.location.hash).toBe('')
  expect(sessionStorage.getItem('pole.invitation.login-handoff')).toBeNull()
}
it.each(['old-first', 'new-first'])(
  'fences same-document preview responses in %s order despite ignored abort',
  async (order) => {
    const old = deferred<Response>(),
      current = deferred<Response>()
    let oldSignal: AbortSignal | undefined
    requests.fetch.mockImplementation((url, init) => {
      if (!String(url).endsWith('/preview')) return requests.unhandled(url, init)
      if (JSON.parse(init!.body as string).token === 'private-invitation-capability') {
        oldSignal = init!.signal as AbortSignal
        return old.promise
      }
      return current.promise
    })
    await act(async () => root.render(<InvitationPage />))
    await openNewInvitation()
    expect(oldSignal!.aborted).toBe(true)
    if (order === 'old-first') {
      await act(async () => old.resolve(await jsonResponse(preview('Old private tenant'))))
      expect(container.textContent).toContain('Loading invitation')
      await act(async () => current.resolve(await jsonResponse(preview('Current tenant'))))
    } else {
      await act(async () => current.resolve(await jsonResponse(preview('Current tenant'))))
      await act(async () => old.resolve(await jsonResponse(preview('Old private tenant'))))
    }
    expect(container.textContent).toContain('Current tenant')
    expect(container.textContent).not.toContain('Old private tenant')
  },
)
it('fences obsolete acceptance effects and sends the new capability after hashchange', async () => {
  identity.authenticated = true
  const accepted = deferred<Response>()
  const tokens: string[] = []
  requests.fetch.mockImplementation((url, init) => {
    const token = JSON.parse(init!.body as string).token as string
    if (String(url).endsWith('/preview'))
      return jsonResponse(preview(token === 'private-invitation-capability' ? 'Old' : 'Current'))
    if (String(url).endsWith('/accept')) {
      tokens.push(token)
      return token === 'private-invitation-capability'
        ? accepted.promise
        : jsonResponse({ title: 'Current invitation requires review' }, 409)
    }
    return requests.unhandled(url, init)
  })
  await act(async () => root.render(<InvitationPage />))
  const accept = async () => {
    const button = [...container.querySelectorAll('button')].find(
      (item) => item.textContent === 'Accept invitation',
    )!
    await act(async () => button.click())
  }
  await accept()
  await openNewInvitation()
  await act(async () =>
    accepted.resolve(
      await jsonResponse({ tenantId: 'obsolete-tenant', defaultTenantId: 'obsolete-tenant' }),
    ),
  )
  expect(localStorage.getItem('pole.activeTenantId')).toBeNull()
  expect(container.textContent).toContain('Current')
  await accept()
  expect(tokens).toEqual(['private-invitation-capability', 'new-private-capability'])
  expect(container.textContent).toContain('Current invitation requires review')
})
it('retains new setup state when obsolete onboarding completes despite ignored abort', async () => {
  const old = deferred<Response>()
  requests.fetch.mockImplementation((url, init) => {
    const token = JSON.parse(init!.body as string).token as string
    if (String(url).endsWith('/preview'))
      return jsonResponse(preview(token === 'private-invitation-capability' ? 'Old' : 'Current'))
    if (String(url).endsWith('/onboarding'))
      return token === 'private-invitation-capability'
        ? old.promise
        : jsonResponse({ setupEmailSent: true })
    return requests.unhandled(url, init)
  })
  await act(async () => root.render(<InvitationPage />))
  const setup = async () => {
    const button = [...container.querySelectorAll('button')].find(
      (item) => item.textContent === 'Set up account or verify email',
    )!
    await act(async () => button.click())
  }
  await setup()
  await openNewInvitation()
  await setup()
  await act(async () => old.resolve(await jsonResponse({ setupEmailSent: false })))
  expect(container.textContent).toContain('Check your email to finish account setup')
  expect(container.textContent).not.toContain('Your account is ready.')
})
