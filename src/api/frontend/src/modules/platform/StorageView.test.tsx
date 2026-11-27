// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

// @vitest-environment jsdom
import { act, useLayoutEffect } from 'react'
import { useQuerySession, type QuerySession } from '../../data/querySession'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { AppShell } from '../../shell/AppShell'
import { createAppRegistry } from '../../shell/moduleRegistry'
import { StorageView } from './PlatformViews'
import { deferred, strictFetch } from '../../test/http'

let session: QuerySession
function StorageProbe() {
  const value = useQuerySession()
  useLayoutEffect(() => {
    session = value
  })
  return <StorageView />
}
let root: Root
let container: HTMLDivElement
const profile = {
  id: 'disk',
  displayName: 'Data disk',
  description: '',
  mediaType: 'SSD',
  performanceTier: 'Standard',
  isEnabled: true,
  isDefault: false,
  targetMappings: [
    { deploymentTargetId: 'a', storageClassName: 'fast' },
    { deploymentTargetId: 'b', storageClassName: 'standard' },
  ],
  revision: 7,
  allowedResourceTypes: ['pole/redis'],
  tenantAccess: { mode: 'AllTenants', tenantIds: [] },
  sizeLimits: null,
}
const storageSnapshot = (id: string) => ({
  deploymentTargetId: id,
  connectionVersion: 1,
  observationVersion: null,
  checkedAtUtc: new Date().toISOString(),
  complete: true,
  storageClasses: [
    { name: id === 'a' ? 'fast' : 'standard', provisioner: 'test', isDefault: true },
  ],
  persistentVolumes: [],
  persistentVolumeClaims: [],
})
const http = strictFetch()
const fetchMock = http.fetch
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  Object.defineProperties(HTMLDialogElement.prototype, {
    showModal: {
      configurable: true,
      value: function (this: HTMLDialogElement) {
        this.open = true
      },
    },
    close: {
      configurable: true,
      value: function (this: HTMLDialogElement) {
        this.open = false
      },
    },
  })
  localStorage.clear()
  window.history.replaceState({}, '', '/')
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
  http.reset()
  fetchMock.mockImplementation((input, init) => {
    const url = new URL(String(input), 'http://localhost')
    const method = init?.method ?? 'GET'
    if (
      !(
        method === 'GET' &&
        ['/deployment-targets', '/profiles', '/observation'].some((path) =>
          url.pathname.endsWith(path),
        )
      ) &&
      !(method === 'PUT' && url.pathname.endsWith('/profiles/disk'))
    )
      return http.unhandled(input, init)
    const value = url.pathname.endsWith('/deployment-targets')
      ? [
          {
            id: 'a',
            displayName: 'East',
            enabled: true,
            acceptsWorkloads: true,
            isDefault: true,
            connection: { kind: 'Kubeconfig', version: 1 },
          },
          {
            id: 'b',
            displayName: 'West',
            enabled: true,
            acceptsWorkloads: true,
            isDefault: false,
            connection: { kind: 'Kubeconfig', version: 1 },
          },
        ]
      : url.pathname.endsWith('/profiles')
        ? [profile]
        : url.pathname.endsWith('/observation')
          ? storageSnapshot(url.searchParams.get('deploymentTargetId')!)
          : init?.method === 'PUT'
            ? { ...profile, revision: 8 }
            : []
    return Promise.resolve(new Response(JSON.stringify(value)))
  })
  vi.stubGlobal('fetch', fetchMock)
})
afterEach(async () => {
  await act(async () => root.unmount())
  container.remove()
  vi.unstubAllGlobals()
  http.verify()
})
async function render(admin = true, token = 'bearer') {
  const tenant = { id: 'alpha', name: 'Alpha', role: 'Owner' as const, isDefault: true }
  const registry = createAppRegistry([
    {
      id: 'storage',
      views: [
        { id: 'storage', label: 'Storage', component: StorageProbe, access: 'authenticated' },
      ],
    },
  ])
  await act(async () =>
    root.render(
      <AppShell
        registry={registry}
        initialViewId="storage"
        api={{ baseUrl: '/api', token }}
        auth={{
          initialized: true,
          authenticated: true,
          userId: 'user',
          isPlatformAdmin: admin,
          login: () => {},
          logout: () => {},
        }}
        tenants={{
          items: [tenant],
          activeTenant: tenant,
          activeTenantId: tenant.id,
          selectTenant: () => {},
          status: 'success',
          error: '',
        }}
      />,
    ),
  )
}
const button = (name: string) =>
  [...container.querySelectorAll<HTMLButtonElement>('button')].find(
    (item) => item.textContent === name,
  )!
const field = (name: string) =>
  [...container.querySelectorAll('label')]
    .find((item) => item.querySelector('.field__label')?.textContent === name)!
    .querySelector<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>(
      'input,select,textarea',
    )!
async function fill(name: string, value: string) {
  await act(async () => {
    const input = field(name)
    const prototype =
      input instanceof HTMLSelectElement
        ? HTMLSelectElement.prototype
        : input instanceof HTMLTextAreaElement
          ? HTMLTextAreaElement.prototype
          : HTMLInputElement.prototype
    Object.getOwnPropertyDescriptor(prototype, 'value')!.set!.call(input, value)
    input.dispatchEvent(
      new Event(input instanceof HTMLSelectElement ? 'change' : 'input', { bubbles: true }),
    )
  })
}

it('uses named target inventory and preserves other mappings and the expected revision on edit', async () => {
  await render()
  expect(container.textContent).toContain('East: fast, West: standard')
  const inventory = fetchMock.mock.calls.filter(([url]) => String(url).includes('/observation'))
  expect(inventory[0][0]).toBe('/api/api/v1/platform/storage/observation?deploymentTargetId=a')
  expect(inventory[0][1]?.headers).toMatchObject({ Authorization: 'Bearer bearer' })
  expect(inventory[0][1]?.headers).not.toHaveProperty('X-Pole-Tenant-ID')
  await act(async () => button('Edit').click())
  await fill('Display name', 'Updated disk')
  await act(async () =>
    field('Display name')
      .closest('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })),
  )
  const update = fetchMock.mock.calls.find(([, init]) => init?.method === 'PUT')!
  expect(JSON.parse(update[1]!.body as string)).toMatchObject({
    displayName: 'Updated disk',
    revision: 7,
  })
  expect(JSON.parse(update[1]!.body as string).targetMappings).toEqual(profile.targetMappings)
  expect(JSON.parse(update[1]!.body as string).targetMappings).toHaveLength(2)
  expect(JSON.parse(update[1]!.body as string)).not.toHaveProperty('storageClassNames')
})

it('waits for the first target discovery before initializing the new profile default', async () => {
  const discovery = deferred<Response>()
  const respond = fetchMock.getMockImplementation()!
  fetchMock.mockImplementation((input, init) =>
    String(input).includes('/observation') ? discovery.promise : respond(input, init),
  )
  await render()
  expect(field('Deployment target').value).toBe('a')
  expect(container.querySelector('form')).toBeNull()
  await act(async () => discovery.resolve(new Response(JSON.stringify(storageSnapshot('a')))))
  expect(field('Storage class').value).toBe('fast')
})

it('retains the mounted profile draft during a slow failed discovery refresh', async () => {
  await render()
  await fill('Display name', 'Unsaved disk')
  const discovery = deferred<Response>()
  const respond = fetchMock.getMockImplementation()!
  fetchMock.mockImplementation((input, init) =>
    String(input).includes('/observation') ? discovery.promise : respond(input, init),
  )
  let refresh!: Promise<unknown>
  await act(async () => {
    refresh = session.client.refetchQueries({
      predicate: (query) => query.queryKey.includes('discovery'),
    })
  })
  expect(field('Display name').value).toBe('Unsaved disk')
  expect(field('Storage class').value).toBe('fast')
  await act(async () => {
    discovery.resolve(new Response('{}', { status: 503 }))
    await refresh
  })
  expect(field('Display name').value).toBe('Unsaved disk')
  expect(field('Storage class').value).toBe('fast')
})

it('uses the accepted profile revision for its next edit even when follow-up inventory fails', async () => {
  let written = false
  const respond = fetchMock.getMockImplementation()!
  fetchMock.mockImplementation((input, init) => {
    if (init?.method === 'PUT') {
      written = true
      const command = JSON.parse(init.body as string) as { revision: number }
      return Promise.resolve(
        new Response(JSON.stringify({ ...profile, ...command, revision: command.revision + 1 })),
      )
    }
    if (written && String(input).endsWith('/profiles'))
      return Promise.resolve(new Response('{}', { status: 503 }))
    return respond(input, init)
  })
  await render()
  await act(async () => button('Edit').click())
  await fill('Description', 'Accepted change')
  await act(async () =>
    field('Description')
      .closest('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })),
  )
  expect(field('Description').value).toBe('Accepted change')
  await fill('Description', 'Next change')
  await act(async () =>
    field('Description')
      .closest('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })),
  )
  const updates = fetchMock.mock.calls.filter(([, init]) => init?.method === 'PUT')
  expect(updates).toHaveLength(2)
  expect(JSON.parse(updates[1][1]!.body as string).revision).toBe(8)
})

it('protects an unsaved profile when changing target and loads only the confirmed target', async () => {
  await render()
  await fill('Display name', 'Unsaved disk')
  await fill('Deployment target', 'b')
  expect(container.querySelector('dialog')).not.toBeNull()
  await act(async () => button('Cancel').click())
  expect(field('Display name').value).toBe('Unsaved disk')
  expect(field('Deployment target').value).toBe('a')
  await fill('Deployment target', 'b')
  await act(async () => button('Discard changes').click())
  expect(field('Deployment target').value).toBe('b')
  expect(field('Display name').value).toBe('')
  expect(
    fetchMock.mock.calls.some(([url]) => String(url).includes('observation?deploymentTargetId=b')),
  ).toBe(true)
})

it('actually clears a new-profile draft only after confirming discard', async () => {
  await render()
  await fill('Display name', 'Unsaved disk')
  await act(async () => button('New profile').click())
  await act(async () => button('Cancel').click())
  expect(field('Display name').value).toBe('Unsaved disk')
  await act(async () => button('New profile').click())
  await act(async () => button('Discard changes').click())
  expect(field('Display name').value).toBe('')
  await act(async () => button('New profile').click())
  expect(container.querySelector('dialog')).toBeNull()
})

it('does not read platform inventory or expose editing to a tenant owner without platform administration', async () => {
  await render(false)
  expect(fetchMock).not.toHaveBeenCalled()
  expect(container.textContent).toContain('Platform administrator access is required')
  expect(container.querySelector('form')).toBeNull()
})

it('clears an administrator draft when platform permission is revoked', async () => {
  await render()
  await fill('Display name', 'Revoked draft')
  await render(false)
  expect(container.querySelector('form')).toBeNull()
  await render()
  expect(field('Display name').value).toBe('')
  await act(async () => button('New profile').click())
  expect(container.querySelector('dialog')).toBeNull()
})

it('retains a dirty profile across token refresh and blocks a changed baseline after background refresh', async () => {
  await render()
  await act(async () => button('Edit').click())
  await fill('Display name', 'Unsaved edit')
  await render(true, 'fresh')
  expect(field('Display name').value).toBe('Unsaved edit')
  const oldRevision = profile.revision
  profile.revision = 8
  try {
    await act(async () =>
      session.client.invalidateQueries({
        predicate: (query) => query.queryKey.includes('platform-storage'),
        refetchType: 'active',
      }),
    )
    expect(field('Display name').value).toBe('Unsaved edit')
    expect(button('Save changes').disabled).toBe(true)
    expect(container.textContent).toContain('This profile changed on the server')
    const refresh = fetchMock.mock.calls
      .filter(([url]) => String(url).endsWith('/profiles'))
      .at(-1)!
    expect(new Headers(refresh[1]?.headers).get('Authorization')).toBe('Bearer fresh')
    await act(async () => button('Refresh').click())
    await act(async () => button('Cancel').click())
    expect(field('Display name').value).toBe('Unsaved edit')
  } finally {
    profile.revision = oldRevision
  }
})

it('removes privileged inventory from the session cache when administrator access is revoked', async () => {
  await render()
  const client = session.client
  expect(
    client
      .getQueryCache()
      .getAll()
      .some((query) => query.queryKey.includes('platform-storage') && query.state.data),
  ).toBe(true)
  await render(false)
  expect(
    client
      .getQueryCache()
      .getAll()
      .filter((query) => query.queryKey.includes('platform-storage'))
      .some((query) => query.state.data),
  ).toBe(false)
  expect(container.textContent).not.toContain('Data disk')
})

it('removes platform evidence when its owning view closes before a later permission change', async () => {
  await render()
  const client = session.client
  await act(async () => root.render(null))
  expect(
    client
      .getQueryCache()
      .getAll()
      .filter((query) => query.queryKey.includes('platform-storage')),
  ).toHaveLength(0)
})
