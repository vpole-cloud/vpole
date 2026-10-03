// @vitest-environment jsdom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { AppShell } from '../../shell/AppShell'
import { createAppRegistry } from '../../shell/moduleRegistry'
import type { ResourcePlacement, SecretMetadata } from '../../types'
import { secretModule } from './SecretModule'

const registry = createAppRegistry([secretModule, { id: 'custom', views: [{ id: 'custom', label: 'Custom resource', component: () => null, access: 'tenant' }], resourceTypes: [{ id: 'test/stateful', label: 'Custom resource', viewId: 'custom' }] }])
const initial: SecretMetadata = { id: 'secret', tenantId: 'alpha', resourceGroupId: 'group', isolationScopeId: 'scope', name: 'application-keys', purpose: 'Connection credentials', keyNames: ['password'], currentVersion: 1, revision: 1, lifecycle: 'Active', createdAtUtc: '2026-01-01T00:00:00Z', updatedAtUtc: '2026-01-01T00:00:00Z' }
const placement = { deploymentId: 'deployment', isolationScopeId: 'scope', deploymentTargetId: 'target', status: 'Applied', desiredOperation: 'Apply' } as ResourcePlacement
let root: Root; let container: HTMLDivElement; let secret: SecretMetadata; let conflict: boolean; let managed: boolean
let placements: ResourcePlacement[]
const fetchMock = vi.fn<(input: RequestInfo | URL, init?: RequestInit) => Promise<Response>>()
const json = (value: unknown) => Promise.resolve(new Response(JSON.stringify(value)))
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  Object.defineProperties(HTMLDialogElement.prototype, { showModal: { configurable: true, value: function (this: HTMLDialogElement) { this.open = true } }, close: { configurable: true, value: function (this: HTMLDialogElement) { this.open = false } } })
  vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => { callback(0); return 1 })
  localStorage.clear(); window.history.replaceState({}, '', '/?view=secrets&resource=catalog')
  container = document.createElement('div'); document.body.append(container); root = createRoot(container)
  secret = structuredClone(initial); conflict = false; managed = false; placements = [placement]
  fetchMock.mockReset().mockImplementation((input, init) => {
    const url = new URL(String(input), 'http://localhost')
    if (url.pathname.endsWith('/secrets/')) return init?.method === 'POST' ? json(secret) : json({ items: [secret], nextCursor: url.searchParams.has('after') ? null : 'next-cursor' })
    if (url.pathname.endsWith('/secrets/secret')) return json(managed ? { ...secret, managedBy: { resourceType: 'test/stateful', resourceId: 'consumer', bindingId: 'owner' } } : secret)
    if (url.pathname.endsWith('/versions')) {
      if (conflict) { secret.currentVersion = 2; return Promise.resolve(new Response(JSON.stringify({ code: 'secrets.version-conflict', detail: 'private-canary' }), { status: 409 })) }
      secret = { ...secret, currentVersion: secret.currentVersion + 1 }; return json(secret)
    }
    if (url.pathname.endsWith('/usage')) return json([{ binding: { id: managed ? 'owner' : 'binding', consumerType: 'test/stateful', consumerId: 'consumer', allDeployments: true, deploymentIds: [], keyMappings: {} }, affectedDeploymentIds: ['deployment'] }])
    if (url.pathname.endsWith('/projections')) return json([
      { resourceDeploymentId: 'deployment', bindingId: 'binding', deploymentTargetId: 'target', desiredVersion: secret.currentVersion, appliedVersion: secret.currentVersion, health: 'Ready', lastAttemptAtUtc: null, failureCode: null },
      { resourceDeploymentId: 'failed', bindingId: 'binding', deploymentTargetId: 'target', desiredVersion: secret.currentVersion, appliedVersion: 1, health: 'Failed', lastAttemptAtUtc: null, failureCode: 'ProjectionUnavailable' },
    ])
    if (url.pathname.endsWith('/resource-groups')) return json({ items: [{ id: 'group', name: 'Applications', tags: [], resourceCount: 1 }], totalCount: 1 })
    if (url.pathname.endsWith('/resource-groups/group')) return json({ id: 'group', name: 'Applications' })
    if (url.pathname.endsWith('/resources')) return json({ items: [{ registrationId: 'registration', resourceId: 'new-consumer', resourceTypeId: 'test/stateful', displayName: 'Custom workload' }], totalCount: 1 })
    if (url.pathname.endsWith('/resource-deployments/')) return json(placements)
    if (url.pathname.endsWith('/isolation-scopes')) return json([{ id: 'scope', resourceGroupId: 'group', name: 'Production', isDefault: true, lifecycle: 'active' }])
    if (url.pathname.endsWith('/deployment-targets/available')) return json([{ id: 'target', displayName: 'Local cluster' }])
    if (url.pathname.endsWith('/bindings')) return json(secret)
    return json([])
  })
  vi.stubGlobal('fetch', fetchMock)
})
afterEach(async () => { await act(async () => root.unmount()); container.remove(); vi.unstubAllGlobals() })
async function render(role: 'Owner' | 'Viewer' = 'Owner', tenantId = 'alpha') {
  const tenant = { id: tenantId, name: tenantId, role, isDefault: true }
  await act(async () => root.render(<AppShell registry={registry} initialViewId="secrets" api={{ baseUrl: 'http://localhost', token: 'token' }} auth={{ initialized: true, authenticated: true, userId: 'user', isPlatformAdmin: false, login: () => {}, logout: () => {} }} tenants={{ items: [tenant], activeTenant: tenant, activeTenantId: tenantId, selectTenant: () => {}, status: 'success', error: '' }} />))
}
const visible = (element: Element) => !element.closest('[hidden]')
const text = () => [...container.querySelectorAll('#app-content *')].filter(visible).map((item) => item.textContent).join(' ')
async function click(name: string) { await act(async () => {
  const scope = container.querySelector('dialog[open]') ?? container
  const element = [...scope.querySelectorAll<HTMLElement>('button,a')].find((item) => visible(item) && (item.textContent === name || item.getAttribute('title') === name || item.getAttribute('aria-label') === name))
  if (!element) throw new Error(`Missing button ${name}`)
  element.click()
}) }
function field(name: string) { return [...container.querySelectorAll('label')].find((label) => visible(label) && label.querySelector('.field__label')?.textContent === name)!.querySelector<HTMLInputElement | HTMLSelectElement>('input,select')! }
async function fill(name: string, value: string) { await act(async () => { const input = field(name); Object.getOwnPropertyDescriptor(input instanceof HTMLSelectElement ? HTMLSelectElement.prototype : HTMLInputElement.prototype, 'value')!.set!.call(input, value); input.dispatchEvent(new Event(input instanceof HTMLSelectElement ? 'change' : 'input', { bubbles: true })) }) }
async function submit() { await act(async () => container.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))) }
async function open(section = 'Overview') { await render(); await click('application-keys'); await click(section) }

it('creates write-only values, persists only navigation and clears a hidden form', async () => {
  await render(); await click('New Secret'); await fill('Name', 'new-key'); await fill('Key 1', 'password'); await fill('Value 1', 'private-canary')
  expect(field('Value 1').type).toBe('password')
  expect(JSON.stringify(localStorage)).not.toContain('private-canary')
  expect(window.location.search).not.toContain('private-canary')
  await act(async () => window.dispatchEvent(new Event('blur')))
  expect(field('Value 1').value).toBe('')
  await submit(); expect(text()).toContain('Enter every value')
  expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'POST')).toBe(false)
  await fill('Value 1', 'private-canary'); await submit()
  expect(JSON.parse(fetchMock.mock.calls.find(([, init]) => init?.method === 'POST')![1]!.body as string)).toEqual({ name: 'new-key', purpose: '', resourceGroupId: 'group', isolationScopeId: 'scope', values: { password: 'private-canary' } })
  expect(container.querySelector('input[type=password]')).toBeNull()
})

it('requires explicit current-version review after a conflict and clears values on section changes', async () => {
  await open('Rotate values'); await fill('Value 1', 'private-canary'); await submit(); conflict = true; await click('Rotate Secret')
  expect(text()).not.toContain('private-canary')
  expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST')).toHaveLength(1)
  await click('Refresh metadata'); expect(container.querySelector<HTMLButtonElement>('button[type=submit]')!.disabled).toBe(true)
  await click('Refresh and review current version'); expect(text()).toContain('version 2')
  conflict = false; await submit(); await click('Rotate Secret')
  const last = fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST').at(-1)!
  expect(JSON.parse(last[1]!.body as string).expectedVersion).toBe(2)
  expect(field('Value 1').value).toBe('')
  await fill('Value 1', 'discard-me'); await click('Overview'); await click('Rotate values'); expect(field('Value 1').value).toBe('')
})

it('keeps managed owner bindings protected and viewer actions unavailable', async () => {
  managed = true; await open('Rotate values'); expect(text()).toContain('Rotate credentials through the owning resource')
  await click('Bindings'); expect(text()).toContain('Protected owner binding'); expect(text()).not.toContain('Detach binding')
  await render('Viewer'); expect(text()).not.toContain('Add binding'); await click('Rotate values'); expect(container.querySelector('input[type=password]')).toBeNull()
})

it('uses generic consumer inventory and rejects deployments spanning isolation scopes', async () => {
  await open('Bindings'); await click('Add binding'); await fill('Consuming resource', 'registration')
  expect(text()).toContain('Custom workload')
  await click('Create binding')
  const post = fetchMock.mock.calls.find(([url, init]) => String(url).endsWith('/bindings') && init?.method === 'POST')!
  expect(JSON.parse(post[1]!.body as string)).toMatchObject({ consumerType: 'test/stateful', consumerId: 'new-consumer', allDeployments: true, deploymentIds: [], keyMappings: {} })
  await click('Add binding'); placements = [placement, { ...placement, deploymentId: 'foreign', isolationScopeId: 'other' }]; await fill('Consuming resource', 'registration')
  expect(text()).toContain('cross this Secret’s isolation scope')
  expect([...container.querySelectorAll<HTMLButtonElement>('button')].find((button) => button.textContent === 'Create binding')!.disabled).toBe(true)
})

it('preserves a successful projection beside a failed target and labels only projection readiness', async () => {
  await open('Projections'); expect(text()).toContain('Projected to target'); expect(text()).toContain('ProjectionUnavailable'); expect(text()).toContain('Failed'); expect(text()).toContain('Application consumption and restart are managed separately')
})

it('supports an explicit deployment and source-key alias without configuring the workload', async () => {
  await open('Bindings'); await click('Add binding'); await fill('Consuming resource', 'registration'); await fill('Deployment selection', 'selected')
  await act(async () => container.querySelector<HTMLInputElement>('input[type=checkbox]')!.click())
  await fill('Key projection', 'mapped'); await fill('Destination for password', 'APP_PASSWORD'); await click('Create binding')
  const post = fetchMock.mock.calls.find(([url, init]) => String(url).endsWith('/bindings') && init?.method === 'POST')!
  expect(JSON.parse(post[1]!.body as string)).toMatchObject({ deploymentIds: ['deployment'], allDeployments: false, keyMappings: { password: 'APP_PASSWORD' } })
  expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'PUT')).toHaveLength(0)
})

it('follows cursor pagination without manufacturing a total count', async () => {
  await render(); await click('Next page'); expect(fetchMock.mock.calls.some(([url]) => String(url).includes('after=next-cursor'))).toBe(true)
  expect(text()).toContain('Page 2'); await click('Previous page'); expect(text()).toContain('Page 1')
})

it('drops a write-only draft after tenant or role changes', async () => {
  await open('Rotate values'); await fill('Value 1', 'private-canary'); await render('Viewer'); expect(container.querySelector('input[type=password]')).toBeNull()
  await render('Owner', 'beta'); expect(container.querySelector<HTMLInputElement>('input[type=password]')?.value ?? '').toBe('')
  expect(JSON.stringify(localStorage)).not.toContain('private-canary')
})
