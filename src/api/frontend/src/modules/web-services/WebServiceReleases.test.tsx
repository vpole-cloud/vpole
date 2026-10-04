// @vitest-environment jsdom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { fetchWebService } from '../../api'
import { useResource } from '../../hooks/useResource'
import { useAppServices } from '../../shell/AppContext'
import { AppShell } from '../../shell/AppShell'
import { createAppRegistry } from '../../shell/moduleRegistry'
import type { WebService, WebServiceRelease } from '../../types'
import { WebServiceReleases } from './WebServiceReleases'

function View() {
  const { api } = useAppServices()
  const details = useResource(`${api.tenantId}:${api.token}`, signal => fetchWebService(api.baseUrl, 'service', api.token, api.tenantId, signal))
  return details.resource && <WebServiceReleases service={details.resource} onChanged={details.refresh} />
}
const registry = createAppRegistry([{ id: 'test', views: [{ id: 'releases', label: 'Releases', component: View, access: 'tenant' }], resourceTypes: [{ id: 'pole/web-service', label: 'Web service', viewId: 'releases' }] }])
const probe = { path: '/', port: '80', initialDelaySeconds: 0, periodSeconds: 5, timeoutSeconds: 1, failureThreshold: 3, successThreshold: 1 }
let service: WebService
let releases: WebServiceRelease[]
let root: Root
let container: HTMLDivElement
let failRead: boolean
let conflict: boolean
let oldRead: Promise<Response> | undefined
const fetchMock = vi.fn<(input: RequestInfo | URL, init?: RequestInit) => Promise<Response>>()
const json = (value: unknown) => Promise.resolve(new Response(JSON.stringify(value)))
function release(revision: number): WebServiceRelease {
  return { id: `release-${revision}`, resourceRevision: revision, action: revision === 11 ? 'SourceBuild' : 'Update', outcome: revision === 13 ? 'Pending' : revision === 12 ? 'Failed' : 'Succeeded',
    requestedAtUtc: new Date().toISOString(), actorUserId: 'actor', actorEmail: 'actor@example.test', traceId: 'trace', correlationId: 'correlation',
    failureMessage: 'raw-failure-canary', spec: { ...service.spec, image: { ...service.spec.image, tag: `revision-${revision}` } },
    provenance: revision === 11 ? { source: 'PoleSourceBuild', commitSha: 'abcdef0123456789', buildUrl: 'javascript:alert(1)', imageDigest: 'sha256:recorded', previousImageTag: 'before', newImageTag: 'after', actorType: 'SourceBuild', deploymentTokenId: null, deploymentTokenName: null, buildJobId: 'build-11', sourceRepositoryId: 'source' } : null,
    deployments: [{ resourceDeploymentId: 'deployment', deploymentTargetId: 'target', resourceRevision: revision, deploymentGeneration: revision, operation: 'Apply', outcome: revision === 12 ? 'Failed' : 'Succeeded', failureCode: revision === 12 ? 'KubernetesApplyFailed' : null, failureMessage: 'raw-target-canary' }] }
}
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  localStorage.clear(); window.history.replaceState(null, '', '/?view=releases')
  container = document.createElement('div'); document.body.append(container); root = createRoot(container)
  Object.defineProperties(HTMLDialogElement.prototype, { showModal: { configurable: true, value: function (this: HTMLDialogElement) { this.open = true } }, close: { configurable: true, value: function (this: HTMLDialogElement) { this.open = false } } })
  service = { id: 'service', name: 'application', resourceGroupId: 'group', activeRevision: 13, exposure: { mode: 'Private', generatedHostnameLabelPrefix: 'webservice' },
    spec: { image: { server: 'registry.test', name: 'app', tag: 'current' }, config: { environmentVariables: { PASSWORD: 'literal-secret-canary' }, portMappings: [], secretEnvironmentReferences: { TOKEN: { secretId: 'secret', key: 'token' } } }, probes: { startup: probe, readiness: probe, liveness: probe }, autoscaling: null },
    deployments: [{ deploymentId: 'deployment', runtimeCellId: 'cell', deploymentTargetId: 'target', desiredGeneration: 13, observedGeneration: 13, deploymentRevision: 13, resourceRevision: 13, fixedReplicas: 1, desiredOperation: 'Apply', status: 'Applied', endpoints: [], compute: { displayName: 'Small', profile: { computeProfileId: 'small', computeProfileRevision: 1 }, requests: { cpuMillis: 100, memoryMi: 128 }, limits: { cpuMillis: 200, memoryMi: 256 }, extendedResources: [] } }] }
  releases = Array.from({ length: 13 }, (_, index) => release(13 - index))
  failRead = false; conflict = false; oldRead = undefined
  fetchMock.mockReset().mockImplementation((input, init) => {
    const url = new URL(String(input))
    if (url.pathname.endsWith('/releases')) {
      if ((init?.headers as Record<string, string>)['X-Pole-Tenant-ID'] === 'old' && oldRead) return oldRead
      if (failRead) return Promise.resolve(new Response('raw-server-canary', { status: 503 }))
      const page = Number(url.searchParams.get('pageNumber'))
      return json({ activeRevision: service.activeRevision, releases: { items: releases.slice((page - 1) * 10, page * 10), totalCount: releases.length, pageNumber: page, pageSize: 10 } })
    }
    if (url.pathname.endsWith('/redeploy') || url.pathname.endsWith('/rollback')) {
      if (conflict) { service = { ...service, activeRevision: 15 }; return Promise.resolve(new Response('raw-server-canary', { status: 409 })) }
      const revision = service.activeRevision + 1
      service = { ...service, activeRevision: revision }
      const entry = { ...release(revision), action: url.pathname.endsWith('/redeploy') ? 'Redeploy' : 'Rollback', outcome: 'Pending' }
      releases.unshift(entry)
      return json({ id: service.id, resourceRevision: revision, releaseId: entry.id, deployments: service.deployments })
    }
    if (url.pathname.endsWith('/service')) return json(service)
    return json([])
  })
  vi.stubGlobal('fetch', fetchMock)
})
afterEach(async () => { await act(async () => root.unmount()); container.remove(); vi.useRealTimers(); vi.unstubAllGlobals() })
async function render(tenantId = 'active', role: 'Owner' | 'Viewer' = 'Owner') {
  const tenant = { id: tenantId, name: tenantId, role, isDefault: true }
  await act(async () => root.render(<AppShell registry={registry} initialViewId="releases" api={{ baseUrl: 'http://localhost', token: 'token' }}
    auth={{ initialized: true, authenticated: true, userId: 'user', isPlatformAdmin: false, login: () => {}, logout: () => {} }}
    tenants={{ items: [tenant], activeTenant: tenant, activeTenantId: tenantId, selectTenant: () => {}, status: 'success', error: '' }} />))
}
const text = () => container.querySelector('#app-content')!.textContent!
const buttons = () => [...container.querySelectorAll('button')]
async function click(label: string) { await act(async () => buttons().find(button => button.textContent === label)!.click()) }
const writes = () => fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST')

it('pages bounded history, explains apply outcomes and links provenance without values or unsafe URLs', async () => {
  await render()
  expect(text()).toContain('Current desired revision 13'); expect(text()).toContain('actor@example.test'); expect(text()).toContain('KubernetesApplyFailed')
  expect(text()).toContain('Recorded digest'); expect(text()).not.toContain('literal-secret-canary'); expect(text()).not.toContain('raw-failure-canary'); expect(text()).not.toContain('raw-target-canary')
  expect(container.querySelector('a[href*="#build-build-11"]')).not.toBeNull(); expect(container.querySelector('a[href^="javascript:"]')).toBeNull()
  await click('Older releases'); expect(text()).toContain('Page 2 of 2'); expect(text()).toContain('Revision 1'); expect(text()).not.toContain('Revision 13 ·')
  const read = fetchMock.mock.calls.find(([url]) => String(url).includes('pageNumber=2'))!
  expect(new URL(String(read[0])).searchParams.get('pageSize')).toBe('10')
  for (const [, init] of fetchMock.mock.calls) expect(init?.headers).toEqual(expect.objectContaining({ Authorization: 'Bearer token', 'X-Pole-Tenant-ID': 'active' }))
})

it('keeps the loaded page and selection after a failed refresh and renders only fixed errors', async () => {
  await render(); await click('Rollback to revision 11'); failRead = true; await click('Refresh releases')
  expect(text()).toContain('Showing previously loaded page 1'); expect(text()).toContain('Revision 13'); expect(text()).not.toContain('raw-server-canary')
  expect(container.querySelector('dialog')?.textContent).toContain('revision 11'); expect(buttons().find(button => button.textContent === 'Confirm rollback')?.disabled).toBe(true)
  expect(writes()).toHaveLength(0)
})

it('confirms one recovery with the captured revision and retains context through conflict and explicit re-confirmation', async () => {
  await render(); await click('Rollback to revision 11')
  expect(container.querySelector('dialog')?.textContent).toContain('Database contents and Secret values are not restored')
  expect(writes()).toHaveLength(0); conflict = true; await click('Confirm rollback')
  expect(JSON.parse(String(writes()[0][1]?.body))).toEqual({ targetRevision: 11, expectedRevision: 13 })
  expect(writes()[0][1]?.headers).toEqual(expect.objectContaining({ 'Content-Type': 'application/json' }))
  expect(container.querySelector('dialog')?.textContent).toContain('Release state changed'); expect(text()).not.toContain('raw-server-canary')
  expect(buttons().find(button => button.textContent === 'Confirm rollback')?.disabled).toBe(true)
  conflict = false; await click('Refresh recovery context'); expect(writes()).toHaveLength(1)
  await click('Confirm rollback'); expect(JSON.parse(String(writes()[1][1]?.body))).toEqual({ targetRevision: 11, expectedRevision: 15 })
  expect(container.querySelector('dialog')).toBeNull(); expect(text()).toContain('Current desired revision 16')
})

it('redeploys without a source rebuild, restores Pending on reload, and polls the saved terminal release', async () => {
  vi.useFakeTimers(); await render(); await click('Redeploy current revision')
  expect(container.querySelector('dialog')?.textContent).toContain('does not rebuild source code')
  await click('Confirm redeploy'); expect(new URL(String(writes()[0][0])).searchParams.get('expectedRevision')).toBe('13')
  await act(async () => root.unmount()); root = createRoot(container); await render()
  expect(container.querySelector('[aria-label="Release revision 14"]')?.textContent).toContain('Pending'); expect(writes()).toHaveLength(1)
  releases[0] = { ...releases[0], outcome: 'Succeeded' }
  await act(async () => { await vi.advanceTimersByTimeAsync(2500) })
  expect(container.querySelector('[aria-label="Release revision 14"]')?.textContent).toContain('Succeeded'); expect(writes()).toHaveLength(1)
})

it('allows Viewer history while hiding recovery actions and ineligible current or failed rollback choices', async () => {
  await render(); expect(buttons().some(button => button.textContent === 'Rollback to revision 13')).toBe(false); expect(buttons().some(button => button.textContent === 'Rollback to revision 12')).toBe(false)
  await render('active', 'Viewer'); expect(text()).toContain('Viewer role'); expect(buttons().some(button => button.textContent?.includes('Rollback') || button.textContent?.includes('Redeploy'))).toBe(false)
  expect(writes()).toHaveLength(0)
})

it('lets a slow Pending history request finish without polling aborting it', async () => {
  vi.useFakeTimers(); await render()
  let complete!: (response: Response) => void
  fetchMock.mockImplementationOnce(() => new Promise(resolve => { complete = resolve }))
  await act(async () => { await vi.advanceTimersByTimeAsync(2500) })
  const inFlight = fetchMock.mock.calls.at(-1)!
  const reads = fetchMock.mock.calls.length
  await act(async () => { await vi.advanceTimersByTimeAsync(10000) })
  expect(fetchMock.mock.calls).toHaveLength(reads)
  expect(inFlight[1]?.signal?.aborted).toBe(false)
  releases[0] = { ...releases[0], outcome: 'Succeeded' }
  await act(async () => complete(new Response(JSON.stringify({ activeRevision: 13, releases: { items: releases.slice(0, 10), totalCount: 13, pageNumber: 1, pageSize: 10 } }))))
  expect(container.querySelector('[aria-label="Release revision 13"]')?.textContent).toContain('Succeeded')
})

it('cancels and clears old tenant history, confirmation and late successful results', async () => {
  let complete!: (response: Response) => void
  oldRead = new Promise(resolve => { complete = resolve }); await render('old')
  const oldCall = fetchMock.mock.calls.find(([url]) => String(url).includes('/releases'))!
  await render('new'); expect(oldCall[1]?.signal?.aborted).toBe(true)
  const foreign = { ...release(11), actorEmail: 'foreign-tenant-canary' }
  await act(async () => complete(new Response(JSON.stringify({ activeRevision: 13, releases: { items: [foreign], totalCount: 1, pageNumber: 1, pageSize: 10 } }))))
  expect(text()).not.toContain('foreign-tenant-canary')
  await click('Rollback to revision 11'); await render('third'); expect(container.querySelector('dialog')).toBeNull(); expect(writes()).toHaveLength(0)
})
