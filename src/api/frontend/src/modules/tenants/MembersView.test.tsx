// @vitest-environment jsdom
import { act } from 'react'
import type { Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { AppContext } from '../../shell/AppContext'
import type { AppServices } from '../../shell/contracts'
import { createTestApp } from '../../test/TestApp'
import { deferred, jsonResponse, strictFetch } from '../../test/http'
import { MembersView } from './MembersView'

const requests = strictFetch()
let root: Root
let container: HTMLDivElement
let current: AppServices
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  requests.reset()
  vi.stubGlobal('fetch', requests.fetch)
  ;({ root, container } = createTestApp())
  current = {
    auth: {
      initialized: true,
      authenticated: true,
      userId: 'user',
      isPlatformAdmin: false,
      login: vi.fn(),
      logout: vi.fn(),
    },
    api: { baseUrl: 'api', token: 'bearer', tenantId: 'alpha' },
    tenant: {
      active: { id: 'alpha', name: 'Alpha', role: 'Owner', isDefault: true },
      canManage: true,
    },
    navigation: {
      viewId: 'members',
      search: '',
      setSearch: vi.fn(),
      goTo: vi.fn(),
      openResource: vi.fn(),
      resourceHref: () => '',
      registerUnsavedChanges: () => () => {},
    },
    theme: { value: 'light', set: vi.fn() },
    notifications: { publish: vi.fn() },
    resourceTypes: [],
  }
  requests.fetch.mockImplementation((url, init) => {
    const path = String(url)
    if (path.endsWith('/members'))
      return jsonResponse([
        {
          id: 'owner',
          tenantId: current.api.tenantId,
          userId: 'owner',
          email: 'owner@example.test',
          role: 'Owner',
          isDefault: true,
        },
      ])
    if (path.endsWith('/invitations')) return jsonResponse([])
    return requests.unhandled(url, init)
  })
})
afterEach(async () => {
  await act(async () => root.unmount())
  container.remove()
  requests.verify()
  vi.unstubAllGlobals()
})
async function render() {
  await act(async () => {
    root.render(
      <AppContext.Provider value={current}>
        <MembersView />
      </AppContext.Provider>,
    )
    await new Promise((resolve) => setTimeout(resolve, 10))
  })
}
it('protects the last owner and allows only owners to offer the Owner role', async () => {
  await render()
  expect(container.textContent).toContain('Last owner')
  expect(
    [...container.querySelectorAll('button')].find((button) => button.textContent === 'Remove')
      ?.disabled,
  ).toBe(true)
  expect([...container.querySelectorAll('option')].some((option) => option.value === 'Owner')).toBe(
    true,
  )
  current = {
    ...current,
    tenant: { active: { ...current.tenant.active!, role: 'Admin' }, canManage: true },
  }
  await render()
  expect([...container.querySelectorAll('option')].some((option) => option.value === 'Owner')).toBe(
    false,
  )
  expect(container.querySelector('button.button--danger')).toBeNull()
})
it('does not request private invitations for viewers and clears a former managers draft', async () => {
  await render()
  const input = container.querySelector('input')!
  await act(async () => {
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(
      input,
      'private@example.test',
    )
    input.dispatchEvent(new Event('input', { bubbles: true }))
  })
  requests.fetch.mockClear()
  current = {
    ...current,
    tenant: { active: { ...current.tenant.active!, role: 'Viewer' }, canManage: false },
  }
  await render()
  expect(container.querySelector('input')).toBeNull()
  expect(container.textContent).not.toContain('Invite member')
  expect(requests.fetch.mock.calls.some(([url]) => String(url).endsWith('/invitations'))).toBe(
    false,
  )
  current = {
    ...current,
    tenant: { active: { ...current.tenant.active!, role: 'Owner' }, canManage: true },
  }
  await render()
  expect(container.querySelector('input')?.value).toBe('')
})
it('never shows another tenants late invitation response', async () => {
  const pending = deferred<Response>()
  const fallback = requests.fetch.getMockImplementation()!
  requests.fetch.mockImplementation((url, init) =>
    String(url) === 'api/api/v1/tenants/alpha/invitations' ? pending.promise : fallback(url, init),
  )
  await render()
  current = {
    ...current,
    api: { ...current.api, tenantId: 'beta' },
    tenant: {
      active: { id: 'beta', name: 'Beta', role: 'Owner', isDefault: false },
      canManage: true,
    },
  }
  await render()
  await act(async () => {
    pending.resolve(
      new Response(
        JSON.stringify([
          {
            id: 'private',
            tenantId: 'alpha',
            email: 'alpha-private@example.test',
            role: 'Viewer',
            expiresAtUtc: '2030-01-01',
            status: 'Pending',
            deliveryStatus: 'Sent',
          },
        ]),
      ),
    )
    await Promise.resolve()
  })
  expect(container.textContent).not.toContain('alpha-private@example.test')
})

it('preserves platform-admin member controls without allowing Viewer invitations', async () => {
  current = {
    ...current,
    auth: { ...current.auth, isPlatformAdmin: true },
    tenant: { active: { ...current.tenant.active!, role: 'Viewer' }, canManage: false },
  }
  await render()
  expect(container.textContent).toContain('Last owner')
  expect(container.querySelector('button.button--danger')).not.toBeNull()
  expect(container.textContent).not.toContain('Invite member')
  expect(container.querySelector('input')).toBeNull()
  current = { ...current, auth: { ...current.auth, isPlatformAdmin: false } }
  await render()
  expect(container.querySelector('button.button--danger')).toBeNull()
})
