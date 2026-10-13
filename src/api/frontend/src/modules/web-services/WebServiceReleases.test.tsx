// @vitest-environment jsdom
import { act } from 'react'
import { deferred, strictFetch } from '../../test/http'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { useWebService } from './useWebService'
import { useAppServices } from '../../shell/AppContext'
import { AppShell } from '../../shell/AppShell'
import { createAppRegistry } from '../../shell/moduleRegistry'
import type { WebService, WebServiceRelease } from '../../types'
import { WebServiceReleases } from './WebServiceReleases'

function View() {
  const { api } = useAppServices()
  const details = useWebService('service', api)
  return details.service && <WebServiceReleases service={details.service} onChanged={details.refresh} />
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
let heldHistory: Promise<Response> | undefined
const requests = strictFetch()
const fetchMock = requests.fetch
const json = (value: unknown) => Promise.resolve(new Response(JSON.stringify(value)))
function release(revision: number): WebServiceRelease {
  return { id: `release-${revision}`, resourceRevision: revision, action: revision === 11 ? 'SourceBuild' : 'Update', outcome: revision === 13 ? 'Pending' : revision === 12 ? 'Failed' : 'Succeeded',
    requestedAtUtc: new Date().toISOString(), actorUserId: 'actor', actorEmail: 'actor@example.test', traceId: 'trace', correlationId: 'correlation',
    failureMessage: 'raw-failure-canary', spec: { ...service.spec, image: { ...service.spec.image, tag: `revision-${revision}` } },
    provenance: revision === 11 ? { source: 'PoleSourceBuild', commitSha: 'abcdef0123456789', buildUrl: 'javascript:alert(1)', imageDigest: 'sha256:recorded', previousImageTag: 'before', newImageTag: 'after', actorType: 'SourceBuild', deploymentTokenId: null, deploymentTokenName: null, buildJobId: 'build-11', sourceRepositoryId: 'source' } : null,
    deployments: [{ resourceDeploymentId: 'deployment', deploymentTargetId: 'target', resourceRevision: revision, deploymentGeneration: revision, operation: 'Apply', outcome: revision === 12 ? 'Failed' : 'Succeeded', failureCode: revision === 12 ? 'KubernetesApplyFailed' : null, failureMessage: 'raw-target-canary' }] }
}
beforeEach(() => {
  requests.reset()
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  localStorage.clear(); window.history.replaceState(null, '', '/?view=releases')
  container = document.createElement('div'); document.body.append(container); root = createRoot(container)
  Object.defineProperties(HTMLDialogElement.prototype, { showModal: { configurable: true, value: function (this: HTMLDialogElement) { this.open = true } }, close: { configurable: true, value: function (this: HTMLDialogElement) { this.open = false } } })
  service = { id: 'service', name: 'application', resourceGroupId: 'group', activeRevision: 13, exposure: { mode: 'Private', generatedHostnameLabelPrefix: 'webservice' },
    spec: { image: { server: 'registry.test', name: 'app', tag: 'current' }, config: { environmentVariables: { PASSWORD: 'literal-secret-canary' }, portMappings: [], secretEnvironmentReferences: { TOKEN: { secretId: 'secret', key: 'token' } } }, probes: { startup: probe, readiness: probe, liveness: probe }, autoscaling: null },
    deployments: [{ deploymentId: 'deployment', runtimeCellId: 'cell', deploymentTargetId: 'target', desiredGeneration: 13, observedGeneration: 13, deploymentRevision: 13, resourceRevision: 13, fixedReplicas: 1, desiredOperation: 'Apply', status: 'Applied', endpoints: [], compute: { displayName: 'Small', profile: { computeProfileId: 'small', computeProfileRevision: 1 }, requests: { cpuMillis: 100, memoryMi: 128 }, limits: { cpuMillis: 200, memoryMi: 256 }, extendedResources: [] } }] }
  releases = Array.from({ length: 13 }, (_, index) => release(13 - index))
  failRead = false; conflict = false; oldRead = undefined; heldHistory = undefined
  fetchMock.mockReset().mockImplementation((input, init) => {
    const url = new URL(String(input))
    if (url.pathname.endsWith('/releases')) {
      if ((init?.headers as Record<string, string>)['X-Pole-Tenant-ID'] === 'old' && oldRead) return oldRead
      if (heldHistory) return heldHistory
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
    return requests.unhandled(input, init)
  })
  vi.stubGlobal('fetch', fetchMock)
})
afterEach(async () => { await act(async () => root.unmount()); container.remove(); requests.verify(); vi.useRealTimers(); vi.unstubAllGlobals() })
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


const historySnapshot = () => ({ activeRevision: service.activeRevision, releases: { items: releases.slice(0, 10), totalCount: releases.length, pageNumber: 1, pageSize: 10 } })

it.each(['redeploy', 'rollback'] as const)('opens the %s confirmation from loaded matching history during a held background refresh', async action => {
  await render()
  const held = deferred<Response>(); heldHistory = held.promise
  await click('Refresh releases')
  const inFlight = fetchMock.mock.calls.filter(([url]) => String(url).includes('/releases')).at(-1)!
  expect(inFlight[1]?.signal?.aborted).toBe(false)
  const choice = buttons().find(button => button.textContent === (action === 'redeploy' ? 'Redeploy current revision' : 'Rollback to revision 11'))!
  expect.soft(choice.disabled).toBe(false)
  await act(async () => choice.click())
  const dialog = container.querySelector('dialog')
  expect(dialog?.textContent).toContain(action === 'redeploy' ? 'Redeploy reapplies current configuration.' : 'Restore the specification saved in revision 11')
  const confirm = buttons().find(button => button.textContent === (action === 'redeploy' ? 'Confirm redeploy' : 'Confirm rollback'))!
  expect(confirm.disabled).toBe(false); expect(writes()).toHaveLength(0)
  heldHistory = undefined
  await act(async () => held.resolve(new Response(JSON.stringify(historySnapshot()))))
  expect(container.querySelector('dialog')).toBe(dialog)
  await click(action === 'redeploy' ? 'Confirm redeploy' : 'Confirm rollback')
  expect(writes()).toHaveLength(1)
  const [url, command] = writes()[0]
  if (action === 'redeploy') expect(new URL(String(url)).searchParams.get('expectedRevision')).toBe('13')
  else expect(JSON.parse(String(command?.body))).toEqual({ targetRevision: 11, expectedRevision: 13 })
  expect(command?.headers).toEqual(expect.objectContaining({ Authorization: 'Bearer token', 'X-Pole-Tenant-ID': 'active' }))
})

it('keeps an already open recovery dialog actionable through a healthy held refresh without resetting its captured selection', async () => {
  await render(); await click('Rollback to revision 11')
  const dialog = container.querySelector('dialog')!
  const held = deferred<Response>(); heldHistory = held.promise
  await click('Refresh releases')
  expect(container.querySelector('dialog')).toBe(dialog); expect(dialog.open).toBe(true)
  expect(dialog.textContent).toContain('revision 11'); expect(text()).toContain('Current desired revision 13')
  expect(buttons().find(button => button.textContent === 'Confirm rollback')!.disabled).toBe(false)
  expect(writes()).toHaveLength(0)
  heldHistory = undefined
  await act(async () => held.resolve(new Response(JSON.stringify(historySnapshot()))))
  expect(container.querySelector('dialog')).toBe(dialog)
})

it('retains confirmation but blocks failed history and a held retry until current history succeeds', async () => {
  await render(); await click('Redeploy current revision')
  const dialog = container.querySelector('dialog')!
  failRead = true; await click('Refresh releases')
  expect(container.querySelector('dialog')).toBe(dialog)
  expect(buttons().find(button => button.textContent === 'Confirm redeploy')!.disabled).toBe(true)
  expect(text()).not.toContain('raw-server-canary')
  const held = deferred<Response>(); heldHistory = held.promise; failRead = false
  await click('Refresh releases')
  expect(container.querySelector('dialog')).toBe(dialog)
  expect(buttons().find(button => button.textContent === 'Confirm redeploy')!.disabled).toBe(true)
  await click('Confirm redeploy'); expect(writes()).toHaveLength(0)
  heldHistory = undefined
  await act(async () => held.resolve(new Response(JSON.stringify(historySnapshot()))))
  expect(buttons().find(button => button.textContent === 'Confirm redeploy')!.disabled).toBe(false)
})

it('keeps a captured revision disabled through a real service/history revision change until explicit review', async () => {
  await render(); await click('Rollback to revision 11')
  const held = deferred<Response>(); heldHistory = held.promise
  service = { ...service, activeRevision: 14 }; releases.unshift({ ...release(14), outcome: 'Succeeded' })
  await click('Refresh releases')
  expect(buttons().find(button => button.textContent === 'Confirm rollback')!.disabled).toBe(true)
  await click('Confirm rollback'); expect(writes()).toHaveLength(0)
  heldHistory = undefined
  await act(async () => held.resolve(new Response(JSON.stringify(historySnapshot()))))
  expect(buttons().find(button => button.textContent === 'Confirm rollback')!.disabled).toBe(true)
  expect(container.querySelector('dialog')?.textContent).toContain('selection changed')
  await click('Refresh recovery context')
  expect(buttons().find(button => button.textContent === 'Confirm rollback')!.disabled).toBe(false)
  await click('Confirm rollback')
  expect(JSON.parse(String(writes()[0][1]?.body))).toEqual({ targetRevision: 11, expectedRevision: 14 })
})

it('blocks recovery before initial history arrives and when every desired target is deleting', async () => {
  const held = deferred<Response>(); heldHistory = held.promise
  await render()
  expect(buttons().find(button => button.textContent === 'Redeploy current revision')!.disabled).toBe(true)
  await click('Redeploy current revision'); expect(container.querySelector('dialog')).toBeNull()
  heldHistory = undefined
  await act(async () => held.resolve(new Response(JSON.stringify(historySnapshot()))))
  service = { ...service, deployments: service.deployments.map(placement => ({ ...placement, desiredOperation: 'Delete' })) }
  await click('Refresh releases')
  expect(buttons().find(button => button.textContent === 'Redeploy current revision')!.disabled).toBe(true)
  await click('Redeploy current revision'); expect(container.querySelector('dialog')).toBeNull(); expect(writes()).toHaveLength(0)
})


it('disables an open recovery when a service refresh makes every desired target deleting at the same revision', async () => {
  await render(); await click('Redeploy current revision')
  const held = deferred<Response>(); heldHistory = held.promise
  service = { ...service, deployments: service.deployments.map(placement => ({ ...placement, desiredOperation: 'Delete' })) }
  await click('Refresh releases')
  expect.soft(buttons().find(button => button.textContent === 'Confirm redeploy')!.disabled).toBe(true)
  await click('Confirm redeploy'); expect(writes()).toHaveLength(0)
  heldHistory = undefined
  await act(async () => held.resolve(new Response(JSON.stringify(historySnapshot()))))
  expect(buttons().find(button => button.textContent === 'Confirm redeploy')!.disabled).toBe(true)
  await click('Confirm redeploy'); expect(writes()).toHaveLength(0)
})
