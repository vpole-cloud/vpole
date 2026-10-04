// @vitest-environment jsdom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { retryResourceDeployment } from '../api'
import { AppShell } from '../shell/AppShell'
import { createAppRegistry } from '../shell/moduleRegistry'
import { useAppServices } from '../shell/AppContext'
import type { ResourceDeploymentOperation, ResourceDeploymentOperationsPage, ResourcePlacement } from '../types'
import { ResourceOperations } from './ResourceOperations'

function View() {
  const { api } = useAppServices()
  return <ResourceOperations filter={{ resourceGroupId: 'group' }} onRetry={(operation, signal) => retryResourceDeployment(api.baseUrl,
    operation.deployment.id, operation.deployment.placement.deploymentRevision!, operation.deployment.placement.desiredOperation!, api.token, api.tenantId, signal)} />
}
const registry = createAppRegistry([{ id: 'test', views: [{ id: 'operations', label: 'Operations', component: View, access: 'tenant' }],
  resourceTypes: [{ id: 'pole/test', label: 'Test', viewId: 'operations' }] }])
const placement = (id: string): ResourcePlacement => ({ deploymentId: id, deploymentTargetId: id, isolationScopeId: 'scope', runtimeCellId: 'cell',
  desiredGeneration: 2, observedGeneration: 2, deploymentRevision: 7, resourceRevision: 2, fixedReplicas: 1, desiredOperation: 'Apply', status: 'Applied',
  compute: { displayName: 'Small', profile: { computeProfileId: 'small', computeProfileRevision: 1 }, requests: { cpuMillis: 100, memoryMi: 128 }, limits: { cpuMillis: 200, memoryMi: 256 }, extendedResources: [] }, endpoints: [] })
const operation = (id: string, failed = false): ResourceDeploymentOperation => ({ deployment: { id, resourceType: 'pole/test', resourceId: id, resourceGroupId: 'group',
  placement: { ...placement(id), status: failed ? 'Failed' : 'Applied', failureCode: failed ? 'KubernetesApplyFailed' : undefined }, createdAtUtc: new Date().toISOString(), updatedAtUtc: new Date().toISOString() },
  resourceDisplayName: id, phase: failed ? 'Failed' : 'Applied', attemptCount: failed ? 2 : 0, sinceUtc: new Date().toISOString(), retryable: failed, retryNotBeforeUtc: null })
const native = (id: string, ready = true) => ({ deploymentId: id, deploymentRevision: 7, desiredGeneration: 2, checkedAtUtc: new Date().toISOString(), observation: {
  kind: 'Observed', ready, rolloutCurrent: ready, desiredReplicas: 1, currentReplicas: ready ? 1 : 2, updatedReplicas: 1, readyReplicas: 1, availableReplicas: 1,
  podsTruncated: false, pods: [{ name: `${id}-pod`, phase: 'Running', ready: true, currentGeneration: ready, restartCount: 3, reasons: ['CrashLoopBackOff'] }], events: [], eventsUnavailableReason: null,
} })
let root: Root
let container: HTMLDivElement
let data: ResourceDeploymentOperationsPage
let nativeReady: boolean
let failedRefresh: boolean
let retryConflict: boolean
let oldRequest: Promise<Response> | undefined
const fetchMock = vi.fn<(input: RequestInfo | URL, init?: RequestInit) => Promise<Response>>()
const json = (value: unknown) => Promise.resolve(new Response(JSON.stringify(value)))
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  localStorage.clear(); window.history.replaceState(null, '', '/?view=operations')
  container = document.createElement('div'); document.body.append(container); root = createRoot(container)
  Object.defineProperties(HTMLDialogElement.prototype, { showModal: { configurable: true, value: function (this: HTMLDialogElement) { this.open = true } }, close: { configurable: true, value: function (this: HTMLDialogElement) { this.open = false } } })
  data = { items: [operation('healthy'), operation('failed', true)], totalCount: 2, pageNumber: 1, pageSize: 10, checkedAtUtc: new Date().toISOString(), counts: { waiting: 0, accepted: 0, running: 0, applied: 1, failed: 1 } }
  nativeReady = true; failedRefresh = false; retryConflict = false; oldRequest = undefined
  fetchMock.mockReset().mockImplementation((input, init) => {
    const url = new URL(String(input))
    if (url.pathname.endsWith('/operations')) {
      if (oldRequest && (init?.headers as Record<string, string>)['X-Pole-Tenant-ID'] === 'old') return oldRequest
      return failedRefresh ? Promise.resolve(new Response('raw-secret-canary', { status: 503 })) : json(data)
    }
    if (url.pathname.endsWith('/observation')) {
      const id = url.pathname.split('/').at(-2)!
      return id === 'healthy' ? json(native(id, nativeReady)) : json({ deploymentId: id, deploymentRevision: 7, desiredGeneration: 2, checkedAtUtc: new Date().toISOString(), observation: { kind: 'Unavailable', reason: 'TargetUnavailable' } })
    }
    if (url.pathname.endsWith('/retry')) {
      if (retryConflict) return Promise.resolve(new Response('raw-secret-canary', { status: 409 }))
      data = { ...data, items: [operation('healthy'), { ...operation('failed'), phase: 'Accepted' }], counts: { ...data.counts, failed: 0, accepted: 1 } }
      return json({})
    }
    return json([])
  })
  vi.stubGlobal('fetch', fetchMock)
})
afterEach(async () => { await act(async () => root.unmount()); container.remove(); vi.unstubAllGlobals() })
async function render(tenantId = 'active', role: 'Owner' | 'Viewer' = 'Owner') {
  const tenant = { id: tenantId, name: tenantId, role, isDefault: true }
  await act(async () => root.render(<AppShell registry={registry} initialViewId="operations" api={{ baseUrl: 'http://localhost', token: 'token' }}
    auth={{ initialized: true, authenticated: true, userId: 'user', isPlatformAdmin: false, login: () => {}, logout: () => {} }}
    tenants={{ items: [tenant], activeTenant: tenant, activeTenantId: tenantId, selectTenant: () => {}, status: 'success', error: '' }} />))
}
const text = () => container.querySelector('#app-content')!.textContent!
async function click(label: string) { await act(async () => [...container.querySelectorAll('button')].find(button => button.textContent === label)!.click()) }

it('separates Applied from current readiness and preserves a failed sibling target', async () => {
  nativeReady = false; await render()
  expect(text()).toContain('Applied'); expect(text()).toContain('Rollout in progress'); expect([...container.querySelectorAll('.status-pill')].some(element => element.textContent === 'Ready')).toBe(false)
  expect(text()).toContain('KubernetesApplyFailed'); expect(text()).toContain('selected target is unavailable')
  expect(text()).toContain('Restarts'); expect(text()).toContain('earlier generation')
  nativeReady = true; await click('Refresh readiness')
  expect(text()).toContain('Ready'); expect(text()).toContain('KubernetesApplyFailed')
  for (const [, init] of fetchMock.mock.calls) expect(init?.headers).toEqual(expect.objectContaining({ Authorization: 'Bearer token', 'X-Pole-Tenant-ID': 'active' }))
})

it('retries only the confirmed failed revision and restores saved operation state on remount', async () => {
  await render(); await click('Retry this apply'); expect(container.querySelector('dialog')?.textContent).toContain('Only deployment failed on target failed')
  await click('Retry selected operation')
  const writes = fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST')
  expect(writes).toHaveLength(1)
  const url = new URL(String(writes[0][0])); expect(url.pathname).toContain('/failed/retry'); expect(url.searchParams.get('expectedRevision')).toBe('7'); expect(url.searchParams.get('expectedOperation')).toBe('Apply')
  await act(async () => root.unmount()); root = createRoot(container); await render()
  expect(text()).toContain('Accepted 1'); expect(text()).toContain('healthy'); expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST')).toHaveLength(1)
})

it('marks failed refresh stale and keeps safe conflict recovery without raw server messages', async () => {
  await render(); retryConflict = true; await click('Retry this apply'); await click('Retry selected operation')
  expect(text()).toContain('operation changed'); expect(text()).not.toContain('raw-secret-canary')
  await click('Cancel'); failedRefresh = true; await click('Refresh operations')
  expect(text()).toContain('Previous operation state is stale'); expect(text()).toContain('healthy')
  expect([...container.querySelectorAll('button')].find(button => button.textContent === 'Retry this apply')?.disabled).toBe(true)
})

it('clears old tenant reads immediately, ignores late results, and keeps viewer actions read-only', async () => {
  let resolve!: (value: Response) => void
  oldRequest = new Promise(complete => { resolve = complete })
  await render('old'); const oldSignal = fetchMock.mock.calls.find(([url]) => String(url).includes('/operations'))![1]!.signal!
  data = { ...data, items: [operation('new-tenant')], counts: { ...data.counts, failed: 0 } }
  await render('new', 'Viewer'); expect(oldSignal.aborted).toBe(true)
  await act(async () => resolve(new Response(JSON.stringify({ ...data, items: [operation('foreign-canary', true)] }))))
  expect(text()).toContain('new-tenant'); expect(text()).not.toContain('foreign-canary'); expect(text()).not.toContain('Retry this apply')
})
