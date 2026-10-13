// @vitest-environment jsdom
import { act, useLayoutEffect } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { SessionQueries } from '../data/SessionQueries'
import { scopedQueryKey, useQuerySession, type QuerySession } from '../data/querySession'
import { RealtimeProvider } from '../realtime/RealtimeProvider'
import { createRealtimeConnection } from '../realtime/connection'
import { useRealtime } from '../realtime/context'
import type { RealtimeController } from '../realtime/controller'
import { createRealtimeSession } from '../realtime/sessionApi'
import type { RealtimeSubscription } from '../realtime/types'
import { AppContext } from '../shell/AppContext'
import type { AppServices } from '../shell/contracts'
import { FakeConnection } from '../test/fakeRealtimeConnection'
import { deferred, jsonResponse, strictFetch } from '../test/http'
import type { ResourceDeploymentDiagnostics, ResourceDeploymentObservation, ResourceDeploymentOperation, ResourcePlacement } from '../types'
import { ResourceHealthTarget } from './ResourceHealth'
import { ResourceRuntimeDiagnostics } from './ResourceRuntimeDiagnostics'

vi.mock('../realtime/connection', () => ({ createRealtimeConnection: vi.fn() }))
vi.mock('../realtime/sessionApi', () => ({ createRealtimeSession: vi.fn(), revokeRealtimeSession: vi.fn(async () => {}) }))
const http = strictFetch()
const initialPlacement: ResourcePlacement = { deploymentId: 'deployment', deploymentTargetId: 'target', isolationScopeId: 'scope', runtimeCellId: 'cell', desiredGeneration: 2, observedGeneration: 2, deploymentRevision: 7, resourceRevision: 3, fixedReplicas: 1, desiredOperation: 'Apply', status: 'Applied', endpoints: [],
  compute: { displayName: 'Small', profile: { computeProfileId: 'small', computeProfileRevision: 1 }, requests: { cpuMillis: 100, memoryMi: 128 }, limits: { cpuMillis: 200, memoryMi: 256 }, extendedResources: [] } }
let root: Root; let container: HTMLDivElement; let connection: FakeConnection; let controller: RealtimeController; let session: QuerySession
let placement: ResourcePlacement; let health: ResourceDeploymentObservation; let diagnostic: ResourceDeploymentDiagnostics
let hidden: boolean; let nativeCovered: boolean; let oldTime: string | undefined
let heldHealth: Promise<Response> | undefined; let heldDiagnostics: Promise<Response> | undefined
let healthSignal: AbortSignal | undefined; let diagnosticSignal: AbortSignal | undefined; let streamCanceled: boolean
let streamController: ReadableStreamDefaultController<Uint8Array>; let streamSignal: AbortSignal | undefined
const encoder = new TextEncoder()
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  vi.useFakeTimers(); vi.clearAllMocks(); http.reset(); localStorage.clear()
  hidden = false; nativeCovered = true; oldTime = undefined; heldHealth = undefined; heldDiagnostics = undefined; streamCanceled = false
  Object.defineProperty(document, 'hidden', { configurable: true, get: () => hidden }); window.dispatchEvent(new Event('pageshow'))
  container = document.createElement('div'); document.body.append(container); root = createRoot(container)
  placement = { ...initialPlacement }
  health = { deploymentId: 'deployment', deploymentRevision: 7, desiredGeneration: 2, observationVersion: 1, checkedAtUtc: new Date().toISOString(), observation: { kind: 'Observed', ready: true, rolloutCurrent: true, desiredReplicas: 1, currentReplicas: 1, updatedReplicas: 1, readyReplicas: 1, availableReplicas: 1, podsTruncated: false, pods: [{ name: 'owned-pod', phase: 'Running', ready: true, currentGeneration: true, restartCount: 0, reasons: [] }], events: [], eventsUnavailableReason: null } }
  diagnostic = { deploymentId: 'deployment', resourceType: 'pole/web-service', resourceId: 'app', targetId: 'target', deploymentRevision: 7, desiredGeneration: 2, observationVersion: 1, checkedAtUtc: new Date().toISOString(), diagnostics: { kind: 'Available', instances: [{ uid: 'owned-uid', name: 'owned-pod', phase: 'Running', ready: true, currentGeneration: true, restartCount: 0, reasons: [], containers: [{ name: 'app', kind: 'Application', previousAvailable: false }], containersTruncated: false }], instancesTruncated: false, events: [], eventsTruncated: false } }
  vi.mocked(createRealtimeConnection).mockImplementation(() => {
    connection = new FakeConnection(); let requests: RealtimeSubscription[] = []
    const coverage = () => requests.map((request, index) => ({ ...request, subscriptionId: String(index), covered: request.topic !== 'runtime-observations' || nativeCovered }))
    connection.subscribe.mockImplementation(async values => { requests = values; return coverage() })
    connection.heartbeat.mockImplementation(async () => coverage())
    connection.unsubscribe.mockImplementation(async () => { requests = [] })
    return connection
  })
  vi.mocked(createRealtimeSession).mockResolvedValue({ expiresAtUtc: new Date(Date.now() + 300000).toISOString() })
  http.fetch.mockImplementation((input, init) => {
    const url = new URL(String(input), 'http://localhost')
    if (init?.method && init.method !== 'GET') return http.unhandled(input, init)
    if (url.pathname === '/api/api/v1/resource-deployments/deployment/observation') { healthSignal = init?.signal ?? undefined; return heldHealth ?? jsonResponse({ ...health, checkedAtUtc: oldTime ?? new Date().toISOString() }) }
    if (url.pathname === '/api/api/v1/resource-deployments/deployment/diagnostics') { diagnosticSignal = init?.signal ?? undefined; return heldDiagnostics ?? jsonResponse({ ...diagnostic, checkedAtUtc: oldTime ?? new Date().toISOString() }) }
    if (url.pathname === '/api/api/v1/resource-deployments/deployment/runtime-logs') {
      streamSignal = init?.signal ?? undefined
      const body = new ReadableStream<Uint8Array>({ start(value) { streamController = value; value.enqueue(encoder.encode(`${JSON.stringify({ kind: 'Opened', text: null, reason: null, outputBytes: 0 })}\n`)) }, cancel() { streamCanceled = true } })
      return Promise.resolve(new Response(body, { headers: { 'Content-Type': 'application/x-ndjson' } }))
    }
    return http.unhandled(input, init)
  })
  vi.stubGlobal('fetch', http.fetch)
})
afterEach(async () => { await act(async () => root.unmount()); container.remove(); window.dispatchEvent(new Event('pageshow')); http.verify(); vi.unstubAllGlobals(); vi.useRealTimers() })
function Scene({ token, active, resourceType }: { token: string; active: boolean; resourceType: string }) {
  const realtime = useRealtime()!; const queries = useQuerySession()
  useLayoutEffect(() => { controller = realtime; session = queries }, [realtime, queries])
  const operation: ResourceDeploymentOperation = { deployment: { id: 'deployment', resourceType, resourceId: 'app', resourceGroupId: 'group', placement, createdAtUtc: new Date().toISOString(), updatedAtUtc: new Date().toISOString() }, resourceDisplayName: 'Application', phase: 'Applied', attemptCount: 0, sinceUtc: new Date().toISOString(), retryable: false, retryNotBeforeUtc: null }
  return <AppContext.Provider value={{ api: { baseUrl: '/api', token, tenantId: 'alpha' } } as AppServices}><ResourceHealthTarget placement={placement} resourceType={resourceType} active={active} /><ResourceRuntimeDiagnostics operation={operation} active={active} /></AppContext.Provider>
}
async function render(token = 'first', active = true, resourceType = 'pole/web-service') { await act(async () => root.render(<SessionQueries identity="/api:true:user"><RealtimeProvider baseUrl="/api" token={token} enabled userId="user"><Scene token={token} active={active} resourceType={resourceType} /></RealtimeProvider></SessionQueries>)) }
const tick = async (ms: number) => { await act(async () => vi.advanceTimersByTimeAsync(ms)) }
const reads = (part: string) => http.fetch.mock.calls.filter(([url]) => new URL(String(url), 'http://localhost').pathname.endsWith(`/${part}`)).length
const readiness = () => container.querySelector('[data-testid=readiness-status]')!.textContent
const instances = () => [...container.querySelectorAll<HTMLSelectElement>('select')].find(item => item.closest('label')?.textContent?.startsWith('Instance'))!
async function click(label: string) { await act(async () => [...container.querySelectorAll<HTMLButtonElement>('button')].find(item => item.textContent === label)!.click()) }
async function select(label: string, value: string) { await act(async () => { const input = [...container.querySelectorAll('label')].find(item => item.textContent?.startsWith(label))!.querySelector('select')!; input.value = value; input.dispatchEvent(new Event('change', { bubbles: true })) }) }
async function open() { await render(); await tick(500); await tick(500); await click('Runtime logs and events'); await tick(500); expect(readiness()).toBe('Ready'); expect(instances().querySelector('option[value=owned-uid]')).not.toBeNull() }
const hint = async (version: number, generation = 2, tenantId = 'alpha', id = 'deployment') => { await act(async () => connection.changed({ version: 1, topic: 'runtime-observations', tenantId, resourceId: id, revision: 7, generation, observationVersion: version })) }

it('coalesces exact deployment snapshot hints, keeps held HTTP alive and fences old versions before cache acceptance', async () => {
  await open()
  const healthKey = scopedQueryKey(session, ['/api', 'alpha'], ['health', 'deployment', 7, 2])
  const diagKey = scopedQueryKey(session, ['/api', 'alpha'], ['runtime-discovery', 'deployment', 'pole/web-service', 'app', 'target', 7, 2])
  const held = deferred<Response>(); const heldD = deferred<Response>(); heldHealth = held.promise; heldDiagnostics = heldD.promise
  const previousHealth = structuredClone(health); const previousDiagnostic = structuredClone(diagnostic)
  let reading!: Promise<void>
  await act(async () => { reading = session.client.refetchQueries({ queryKey: scopedQueryKey(session, ['/api', 'alpha'], []), predicate: query => [healthKey, diagKey].some(key => JSON.stringify(key) === JSON.stringify(query.queryKey)) }, { cancelRefetch: false }) })
  const count = reads('observation'); const countD = reads('diagnostics')
  if (health.observation.kind !== 'Observed' || diagnostic.diagnostics.kind !== 'Available') throw new Error('Expected complete native fixture')
  health = { ...health, observationVersion: 4, observation: { ...health.observation, ready: false, rolloutCurrent: false } }
  diagnostic = { ...diagnostic, observationVersion: 4, diagnostics: { ...diagnostic.diagnostics, instances: [{ ...diagnostic.diagnostics.instances[0], uid: 'new-uid', name: 'replacement-pod' }] } }
  await hint(2); await hint(3); await hint(4); await tick(250); await hint(3); await tick(250)
  expect(readiness()).toBe('Stale observation'); expect(instances().querySelector('option[value=owned-uid]')).not.toBeNull()
  expect(healthSignal?.aborted).toBe(false); expect(diagnosticSignal?.aborted).toBe(false)
  expect(reads('observation')).toBe(count); expect(reads('diagnostics')).toBe(countD)
  heldHealth = undefined; heldDiagnostics = undefined
  await act(async () => { held.resolve(new Response(JSON.stringify(previousHealth))); heldD.resolve(new Response(JSON.stringify(previousDiagnostic))); await reading })
  await tick(250)
  expect(reads('observation')).toBe(count + 1); expect(reads('diagnostics')).toBe(countD + 1)
  expect(readiness()).toBe('Rollout in progress'); expect(instances().querySelector('option[value=owned-uid]')).toBeNull(); expect(instances().querySelector('option[value=new-uid]')).not.toBeNull()
  expect(JSON.stringify(session.client.getQueryData(healthKey))).toContain('"observationVersion":4')
  expect(JSON.stringify(session.client.getQueryData(diagKey))).toContain('"observationVersion":4')
})

it('isolates old revision/generation, foreign tenant and logical resource hints and keeps inactive snapshots stale only', async () => {
  await open(); http.fetch.mockClear()
  const oldKey = scopedQueryKey(session, ['/api', 'alpha'], ['health', 'deployment', 7, 1]); session.client.setQueryData(oldKey, { value: { snapshot: health, epoch: 0 }, identity: JSON.stringify(oldKey) })
  await hint(99, 1); await hint(100, 2, 'beta'); await hint(100, 2, 'alpha', 'app'); await tick(500)
  expect(http.fetch).not.toHaveBeenCalled(); expect(readiness()).toBe('Ready'); expect(session.client.getQueryState(oldKey)?.isInvalidated).toBe(true)
  expect(controller.observationReadState('/api', { tenantId: 'alpha', deploymentId: 'deployment', revision: 7, generation: 2 }).floor).toBe(0)
  await render('first', false); http.fetch.mockClear(); await hint(1); await tick(120000)
  expect(http.fetch).not.toHaveBeenCalled(); expect(connection.unsubscribe).toHaveBeenCalled()
})

it('validates covered snapshots every30seconds without heartbeat GET churn and restores20second live fallback on coverage loss', async () => {
  await open(); const count = reads('observation'); const countD = reads('diagnostics')
  await tick(27000); expect(reads('observation')).toBe(count); expect(reads('diagnostics')).toBe(countD)
  await tick(3000); expect(reads('observation')).toBe(count + 1); expect(reads('diagnostics')).toBe(countD + 1); expect(readiness()).toBe('Ready')
  nativeCovered = false
  await act(async () => connection.reconnecting()); await act(async () => connection.reconnected()); await tick(500)
  const fallback = reads('observation'); await tick(20000); expect(reads('observation')).toBe(fallback + 1); expect(readiness()).toBe('Ready')
  await act(async () => { hidden = true; document.dispatchEvent(new Event('visibilitychange')) })
  http.fetch.mockClear(); await tick(120000); expect(http.fetch).not.toHaveBeenCalled()
})

it('retains logs on token refresh but immediately aborts and clears a revoked tenant origin without waiting for unmount', async () => {
  await open(); await select('Instance', 'owned-uid'); await select('Container', 'app'); await click('Follow runtime logs')
  await act(async () => streamController.enqueue(encoder.encode(`${JSON.stringify({ kind: 'Output', text: 'ephemeral-runtime-canary', reason: null, outputBytes: 24 })}\n`)))
  const original = session
  await render('second'); await tick(500)
  expect(streamCanceled).toBe(false); expect(container.textContent).toContain('ephemeral-runtime-canary'); expect(session).toBe(original)
  expect(JSON.stringify(session.client.getQueryCache().getAll().map(query => query.state.data))).not.toContain('ephemeral-runtime-canary')
  await act(async () => connection.accessRevoked('alpha'))
  expect(streamSignal?.aborted).toBe(true); expect(streamCanceled).toBe(true); expect(container.textContent).not.toContain('ephemeral-runtime-canary')
  expect(http.fetch.mock.calls.every(([, init]) => new Headers(init?.headers).get('X-Pole-Tenant-ID') === 'alpha')).toBe(true)
})

it('preserves45second stale evidence and leaves unknown provider native interests uncovered', async () => {
  oldTime = new Date(Date.now() - 46000).toISOString()
  await render(); await tick(500); await tick(500); expect(readiness()).toBe('Stale observation')
  oldTime = undefined
  await render('first', true, 'custom/provider'); await tick(500)
  expect(connection.unsubscribe).toHaveBeenCalled()
  expect(controller.covered({ topic: 'runtime-observations', tenantId: 'alpha', resourceId: 'deployment' })).toBe(false)
  const count = reads('observation'); await tick(20000); expect(reads('observation')).toBe(count + 1)
})

it('normalizes a missing wire revision consistently in snapshot keys, hint matching and diagnostics', async () => {
  Object.defineProperty(placement, 'deploymentRevision', { value: undefined, configurable: true })
  health = { ...health, deploymentRevision: 1 }
  diagnostic = { ...diagnostic, deploymentRevision: 1 }
  await open()
  const count = reads('observation'); const countD = reads('diagnostics')
  health = { ...health, observationVersion: 2 }
  diagnostic = { ...diagnostic, observationVersion: 2 }
  await act(async () => connection.changed({ version: 1, topic: 'runtime-observations', tenantId: 'alpha', resourceId: 'deployment', revision: 1, generation: 2, observationVersion: 2 }))
  await tick(500)
  expect(reads('observation')).toBe(count + 1); expect(reads('diagnostics')).toBe(countD + 1)
  expect(readiness()).toBe('Ready'); expect(instances().querySelector('option[value=owned-uid]')).not.toBeNull()
  const key = scopedQueryKey(session, ['/api', 'alpha'], ['runtime-discovery', 'deployment', 'pole/web-service', 'app', 'target', 1, 2])
  expect(JSON.stringify(session.client.getQueryData(key))).toContain('"observationVersion":2')
})

function SharedSnapshots({ first, show }: { first: boolean; show: boolean }) {
  const realtime = useRealtime()!; const queries = useQuerySession()
  useLayoutEffect(() => { controller = realtime; session = queries }, [realtime, queries])
  useLayoutEffect(() => realtime.register({ topic: 'runtime-observations', tenantId: 'alpha', resourceId: 'deployment' }), [realtime])
  return <AppContext.Provider value={{ api: { baseUrl: '/api', token: 'first', tenantId: 'alpha' } } as AppServices}>
    {show && first && <ResourceHealthTarget key="initiator" placement={placement} resourceType="pole/web-service" active />}
    {show && <ResourceHealthTarget key="survivor" placement={placement} resourceType="pole/web-service" active />}
  </AppContext.Provider>
}
async function renderShared(first: boolean, show: boolean) {
  await act(async () => root.render(<SessionQueries identity="/api:true:user"><RealtimeProvider baseUrl="/api" token="first" enabled userId="user"><SharedSnapshots first={first} show={show} /></RealtimeProvider></SessionQueries>))
}
it('accepts a shared snapshot for the surviving observer when its initiating observer unmounts', async () => {
  await renderShared(true, false); await tick(1000)
  expect(controller.covered({ topic: 'runtime-observations', tenantId: 'alpha', resourceId: 'deployment' })).toBe(true)
  const held = deferred<Response>(); heldHealth = held.promise
  await renderShared(true, true); await tick(100)
  expect(reads('observation')).toBe(1)
  await renderShared(false, true)
  expect(healthSignal?.aborted).toBe(false)
  await act(async () => held.resolve(new Response(JSON.stringify(health))))
  await tick(100)
  expect(reads('observation')).toBe(1)
  expect(readiness()).toBe('Ready')
  const key = scopedQueryKey(session, ['/api', 'alpha'], ['health', 'deployment', 7, 2])
  expect(session.client.getQueryState(key)?.error).toBeNull()
})

it.each(['pole/redis', 'pole/postgresql'])('uses acknowledged runtime observations for%s and keeps20second fallback when native coverage is unavailable', async resourceType => {
  diagnostic = { ...diagnostic, resourceType }
  await render('first', true, resourceType); await tick(1000)
  expect(controller.covered({ topic: 'runtime-observations', tenantId: 'alpha', resourceId: 'deployment' })).toBe(true)
  await click('Runtime logs and events'); await tick(500)
  const count = reads('observation'); const diagnostics = reads('diagnostics')
  await tick(30000); expect(reads('observation')).toBe(count + 1); expect(reads('diagnostics')).toBe(diagnostics + 1)
  health = { ...health, observationVersion: 2, observation: { kind: 'Unavailable', reason: 'NotFound' } }; diagnostic = { ...diagnostic, observationVersion: 2, diagnostics: { kind: 'Available', instances: [], instancesTruncated: false, events: [], eventsTruncated: false } }
  await hint(2); await tick(500); expect(readiness()).toBe('Readiness unavailable'); expect(instances()).toBeUndefined()
  nativeCovered = false; await act(async () => { connection.reconnecting(); connection.reconnected() }); await tick(500)
  expect(controller.covered({ topic: 'runtime-observations', tenantId: 'alpha', resourceId: 'deployment' })).toBe(false)
  const fallback = reads('observation'); await tick(20000); expect(reads('observation')).toBe(fallback + 1)
})
