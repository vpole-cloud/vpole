// @vitest-environment jsdom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { AppShell } from '../../shell/AppShell'
import { createAppRegistry } from '../../shell/moduleRegistry'
import { StorageView } from './PlatformViews'

let root: Root
let container: HTMLDivElement
const profile = { id: 'disk', displayName: 'Data disk', description: '', mediaType: 'SSD', performanceTier: 'Standard', isEnabled: true, isDefault: false,
  targetMappings: [{ deploymentTargetId: 'a', storageClassName: 'fast' }, { deploymentTargetId: 'b', storageClassName: 'standard' }],
  revision: 7, allowedResourceTypes: ['pole/redis'], tenantAccess: { mode: 'AllTenants', tenantIds: [] }, sizeLimits: null }
const fetchMock = vi.fn<(input: RequestInfo | URL, init?: RequestInit) => Promise<Response>>()
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  Object.defineProperties(HTMLDialogElement.prototype, {
    showModal: { configurable: true, value: function (this: HTMLDialogElement) { this.open = true } },
    close: { configurable: true, value: function (this: HTMLDialogElement) { this.open = false } },
  })
  localStorage.clear(); window.history.replaceState({}, '', '/')
  container = document.createElement('div'); document.body.append(container); root = createRoot(container)
  fetchMock.mockReset().mockImplementation((input, init) => {
    const url = new URL(String(input), 'http://localhost')
    const value = url.pathname.endsWith('/deployment-targets')
      ? [{ id: 'a', displayName: 'East', enabled: true, acceptsWorkloads: true, isDefault: true }, { id: 'b', displayName: 'West', enabled: true, acceptsWorkloads: true, isDefault: false }]
      : url.pathname.endsWith('/profiles') ? [profile]
      : url.pathname.endsWith('/storage-classes') ? [{ name: url.searchParams.get('deploymentTargetId') === 'a' ? 'fast' : 'standard', provisioner: 'test', isDefault: true }]
      : init?.method === 'PUT' ? { ...profile, revision: 8 } : []
    return Promise.resolve(new Response(JSON.stringify(value)))
  })
  vi.stubGlobal('fetch', fetchMock)
})
afterEach(async () => { await act(async () => root.unmount()); container.remove(); vi.unstubAllGlobals() })
async function render(admin = true) {
  const tenant = { id: 'alpha', name: 'Alpha', role: 'Owner' as const, isDefault: true }
  const registry = createAppRegistry([{ id: 'storage', views: [{ id: 'storage', label: 'Storage', component: StorageView, access: 'authenticated' }] }])
  await act(async () => root.render(<AppShell registry={registry} initialViewId="storage" api={{ baseUrl: '/api', token: 'bearer' }}
    auth={{ initialized: true, authenticated: true, userId: 'user', isPlatformAdmin: admin, login: () => {}, logout: () => {} }}
    tenants={{ items: [tenant], activeTenant: tenant, activeTenantId: tenant.id, selectTenant: () => {}, status: 'success', error: '' }} />))
}
const button = (name: string) => [...container.querySelectorAll<HTMLButtonElement>('button')].find((item) => item.textContent === name)!
const field = (name: string) => [...container.querySelectorAll('label')].find((item) => item.querySelector('.field__label')?.textContent === name)!.querySelector<HTMLInputElement | HTMLSelectElement>('input,select')!
async function fill(name: string, value: string) {
  await act(async () => { const input = field(name); Object.getOwnPropertyDescriptor(input instanceof HTMLSelectElement ? HTMLSelectElement.prototype : HTMLInputElement.prototype, 'value')!.set!.call(input, value); input.dispatchEvent(new Event(input instanceof HTMLSelectElement ? 'change' : 'input', { bubbles: true })) })
}

it('uses named target inventory and preserves other mappings and the expected revision on edit', async () => {
  await render()
  expect(container.textContent).toContain('East: fast, West: standard')
  const inventory = fetchMock.mock.calls.filter(([url]) => String(url).includes('storage-classes'))
  expect(inventory[0][0]).toBe('/api/api/v1/platform/storage/storage-classes?deploymentTargetId=a')
  expect(inventory[0][1]?.headers).toMatchObject({ Authorization: 'Bearer bearer' })
  expect(inventory[0][1]?.headers).not.toHaveProperty('X-Pole-Tenant-ID')
  await act(async () => button('Edit').click())
  await fill('Display name', 'Updated disk')
  await act(async () => field('Display name').closest('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })))
  const update = fetchMock.mock.calls.find(([, init]) => init?.method === 'PUT')!
  expect(JSON.parse(update[1]!.body as string)).toMatchObject({ displayName: 'Updated disk', revision: 7 })
  expect(JSON.parse(update[1]!.body as string).targetMappings).toEqual(profile.targetMappings)
  expect(JSON.parse(update[1]!.body as string).targetMappings).toHaveLength(2)
  expect(JSON.parse(update[1]!.body as string)).not.toHaveProperty('storageClassNames')
})

it('protects an unsaved profile when changing target and loads only the confirmed target', async () => {
  await render(); await fill('Display name', 'Unsaved disk'); await fill('Deployment target', 'b')
  expect(container.querySelector('dialog')).not.toBeNull()
  await act(async () => button('Cancel').click())
  expect(field('Display name').value).toBe('Unsaved disk'); expect(field('Deployment target').value).toBe('a')
  await fill('Deployment target', 'b'); await act(async () => button('Discard changes').click())
  expect(field('Deployment target').value).toBe('b'); expect(field('Display name').value).toBe('')
  expect(fetchMock.mock.calls.some(([url]) => String(url).includes('storage-classes?deploymentTargetId=b'))).toBe(true)
})

it('actually clears a new-profile draft only after confirming discard', async () => {
  await render(); await fill('Display name', 'Unsaved disk')
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
  await render(); await fill('Display name', 'Revoked draft'); await render(false)
  expect(container.querySelector('form')).toBeNull()
  await render()
  expect(field('Display name').value).toBe('')
  await act(async () => button('New profile').click())
  expect(container.querySelector('dialog')).toBeNull()
})
