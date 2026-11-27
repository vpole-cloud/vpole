// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

// @vitest-environment jsdom
import { act, useLayoutEffect } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { SessionQueries } from '../../data/SessionQueries'
import { scopedQueryKey, useQuerySession, type QuerySession } from '../../data/querySession'
import { RealtimeProvider } from '../../realtime/RealtimeProvider'
import { createRealtimeConnection } from '../../realtime/connection'
import { createRealtimeSession } from '../../realtime/sessionApi'
import { AppShell } from '../../shell/AppShell'
import { createAppRegistry } from '../../shell/moduleRegistry'
import { FakeConnection } from '../../test/fakeRealtimeConnection'
import { deferred, jsonResponse, strictFetch } from '../../test/http'
import type { SecretMetadata } from '../../types'
import { secretModule } from './SecretModule'
import { SecretsView } from './SecretsView'

vi.mock('../../realtime/connection', () => ({ createRealtimeConnection: vi.fn() }))
vi.mock('../../realtime/sessionApi', () => ({
  createRealtimeSession: vi.fn(),
  revokeRealtimeSession: vi.fn(async () => {}),
}))
const initial: SecretMetadata = {
  id: 'secret',
  tenantId: 'alpha',
  resourceGroupId: 'group',
  isolationScopeId: 'scope',
  name: 'application-keys',
  purpose: '',
  keyNames: ['password'],
  currentVersion: 1,
  revision: 1,
  lifecycle: 'Active',
  createdAtUtc: '2026-01-01T00:00:00Z',
  updatedAtUtc: '2026-01-01T00:00:00Z',
}
const http = strictFetch()
let root: Root
let container: HTMLDivElement
let session: QuerySession
let connection: FakeConnection
let secret: SecretMetadata
let hidden: boolean
let heldDetail: Promise<Response> | undefined
let detailSignal: AbortSignal | undefined
function View() {
  const queries = useQuerySession()
  useLayoutEffect(() => {
    session = queries
  })
  return <SecretsView />
}
const registry = createAppRegistry([
  { ...secretModule, views: secretModule.views.map((view) => ({ ...view, component: View })) },
])
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  vi.useFakeTimers()
  vi.clearAllMocks()
  localStorage.clear()
  http.reset()
  hidden = false
  Object.defineProperty(document, 'hidden', { configurable: true, get: () => hidden })
  window.dispatchEvent(new Event('pageshow'))
  window.history.replaceState({}, '', '/?view=secrets&resource=catalog')
  vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => {
    callback(0)
    return 1
  })
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
  secret = { ...initial }
  heldDetail = undefined
  detailSignal = undefined
  vi.mocked(createRealtimeConnection).mockImplementation(() => {
    connection = new FakeConnection()
    return connection
  })
  vi.mocked(createRealtimeSession).mockResolvedValue({
    expiresAtUtc: new Date(Date.now() + 300000).toISOString(),
  })
  http.fetch.mockImplementation((input, init) => {
    const url = new URL(String(input), 'http://localhost')
    if (init?.method && init.method !== 'GET') return http.unhandled(input, init)
    if (url.pathname === '/api/api/v1/secrets/')
      return jsonResponse({ items: [secret], nextCursor: null })
    if (url.pathname === '/api/api/v1/secrets/secret') {
      detailSignal = init?.signal ?? undefined
      return heldDetail ?? jsonResponse(secret)
    }
    if (url.pathname === '/api/api/v1/resource-groups/group')
      return jsonResponse({ id: 'group', name: 'Applications' })
    if (url.pathname === '/api/api/v1/resource-groups/group/isolation-scopes')
      return jsonResponse([
        {
          id: 'scope',
          resourceGroupId: 'group',
          name: 'Production',
          lifecycle: 'active',
          isDefault: true,
        },
      ])
    if (url.pathname === '/api/api/v1/secrets/secret/usage') return jsonResponse([])
    if (url.pathname === '/api/api/v1/secrets/secret/projections') return jsonResponse([])
    return http.unhandled(input, init)
  })
  vi.stubGlobal('fetch', http.fetch)
})
afterEach(async () => {
  await act(async () => root.unmount())
  container.remove()
  window.dispatchEvent(new Event('pageshow'))
  http.verify()
  vi.unstubAllGlobals()
  vi.useRealTimers()
})
async function render() {
  const tenant = { id: 'alpha', name: 'Alpha', role: 'Member' as const, isDefault: true }
  await act(async () =>
    root.render(
      <SessionQueries identity="/api:true:user">
        <RealtimeProvider baseUrl="/api" token="test-credential" enabled userId="user">
          <AppShell
            registry={registry}
            initialViewId="secrets"
            api={{ baseUrl: '/api', token: 'test-credential' }}
            auth={{
              initialized: true,
              authenticated: true,
              userId: 'user',
              isPlatformAdmin: false,
              login: () => {},
              logout: () => {},
            }}
            tenants={{
              items: [tenant],
              activeTenant: tenant,
              activeTenantId: 'alpha',
              selectTenant: () => {},
              status: 'success',
              error: '',
            }}
          />
        </RealtimeProvider>
      </SessionQueries>,
    ),
  )
  await tick(500)
}
const visible = (element: Element) => !element.closest('[hidden]')
async function click(label: string) {
  await act(async () => {
    const target = [...container.querySelectorAll<HTMLElement>('button,a')].find(
      (item) => visible(item) && item.textContent === label,
    )
    if (!target) throw new Error(`Missing action ${label}`)
    target.click()
  })
}
const field = () =>
  [...container.querySelectorAll('label')]
    .find(
      (item) => visible(item) && item.querySelector('.field__label')?.textContent === 'Value 1',
    )!
    .querySelector<HTMLInputElement>('input')!
async function fill(value: string) {
  await act(async () => {
    const input = field()
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(input, value)
    input.dispatchEvent(new Event('input', { bubbles: true }))
  })
}
const tick = async (ms: number) => {
  await act(async () => vi.advanceTimersByTimeAsync(ms))
}
const reads = (suffix: string) =>
  http.fetch.mock.calls.filter(([url]) =>
    new URL(String(url), 'http://localhost').pathname.endsWith(suffix),
  ).length
const changed = async (resourceId = 'secret', tenantId = 'alpha') => {
  await act(async () => connection.changed({ version: 1, topic: 'secrets', tenantId, resourceId }))
}
async function open(section: string) {
  await render()
  await click('application-keys')
  await click(section)
  await tick(500)
}

it('refreshes scoped metadata while retaining an uncached rotation draft, requiring review, and silencing hidden observers', async () => {
  await open('Rotate values')
  await fill('local-value-canary')
  const detailKey = scopedQueryKey(session, ['/api', 'alpha'], ['secrets', 'detail', 'secret'])
  const inventoryKey = scopedQueryKey(
    session,
    ['/api', 'alpha'],
    ['secrets', 'inventory', undefined],
  )
  const foreignKeys = [
    scopedQueryKey(session, ['/api', 'beta'], ['secrets', 'detail', 'secret']),
    scopedQueryKey(session, ['/other-api', 'alpha'], ['secrets', 'detail', 'secret']),
  ]
  for (const key of foreignKeys)
    session.client.setQueryData(key, { value: initial, identity: JSON.stringify(key) })
  const detailCount = reads('/secrets/secret')
  const inventoryCount = reads('/secrets/')
  await changed('secret', 'beta')
  await changed('other-secret')
  await tick(250)
  expect(reads('/secrets/secret')).toBe(detailCount)
  secret = { ...secret, currentVersion: 2, revision: 2 }
  await changed()
  await tick(250)
  expect(field().value).toBe('local-value-canary')
  expect(container.textContent).toContain(
    'The current version must be reviewed before resubmitting',
  )
  expect(container.querySelector<HTMLButtonElement>('button[type=submit]')!.disabled).toBe(true)
  expect(
    session.client.getQueryData<{ value: SecretMetadata }>(detailKey)?.value.currentVersion,
  ).toBe(2)
  expect(reads('/secrets/')).toBe(inventoryCount)
  expect(session.client.getQueryState(inventoryKey)?.isInvalidated).toBe(true)
  for (const key of foreignKeys)
    expect(session.client.getQueryState(key)?.isInvalidated).toBe(false)
  expect(
    JSON.stringify(
      session.client
        .getQueryCache()
        .getAll()
        .map((query) => query.state),
    ),
  ).not.toContain('local-value-canary')
  expect(session.client.getMutationCache().getAll()).toHaveLength(0)
  expect(JSON.stringify(localStorage)).not.toContain('local-value-canary')
  expect(window.location.href).not.toContain('local-value-canary')
  const request = http.fetch.mock.calls.find(([url]) => String(url).endsWith('/secrets/secret'))!
  expect(new Headers(request[1]?.headers).get('X-Pole-Tenant-ID')).toBe('alpha')
  await act(async () => {
    hidden = true
    document.dispatchEvent(new Event('visibilitychange'))
  })
  expect(field().value).toBe('')
  http.fetch.mockClear()
  await changed()
  await tick(120000)
  expect(http.fetch).not.toHaveBeenCalled()
  expect(connection.stop).toHaveBeenCalled()
})

it('keeps a slow metadata read alive and performs exactly one trailing read after a burst', async () => {
  await open('Overview')
  const held = deferred<Response>()
  heldDetail = held.promise
  const key = scopedQueryKey(session, ['/api', 'alpha'], ['secrets', 'detail', 'secret'])
  let reading!: Promise<void>
  await act(async () => {
    reading = session.client.refetchQueries(
      { queryKey: key, exact: true },
      { cancelRefetch: false },
    )
  })
  const count = reads('/secrets/secret')
  secret = { ...secret, currentVersion: 3, revision: 3 }
  await changed()
  await changed()
  await tick(250)
  await changed()
  await tick(250)
  expect(reads('/secrets/secret')).toBe(count)
  expect(detailSignal?.aborted).toBe(false)
  heldDetail = undefined
  await act(async () => {
    held.resolve(new Response(JSON.stringify(initial)))
    await reading
  })
  await tick(250)
  expect(reads('/secrets/secret')).toBe(count + 1)
  expect(session.client.getQueryData<{ value: SecretMetadata }>(key)?.value.currentVersion).toBe(3)
})

it('uses 60 second metadata safety reads while covered and preserves the projection fallback interval when disconnected', async () => {
  await open('Projections')
  const count = reads('/secrets/secret/projections')
  await tick(59000)
  expect(reads('/secrets/secret/projections')).toBe(count)
  await tick(1000)
  expect(reads('/secrets/secret/projections')).toBe(count + 1)
  await act(async () => connection.reconnecting())
  const fallback = reads('/secrets/secret/projections')
  await tick(2500)
  expect(reads('/secrets/secret/projections')).toBe(fallback + 1)
})
