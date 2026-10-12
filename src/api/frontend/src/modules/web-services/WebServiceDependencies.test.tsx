// @vitest-environment jsdom
import type { QueryClient } from '@tanstack/react-query'
import { useQuerySession } from '../../data/querySession'
import { act, useLayoutEffect } from 'react'
import { deferred, strictFetch } from '../../test/http'
import type { Root } from 'react-dom/client'
import { createTestApp } from '../../test/TestApp'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { AppContext } from '../../shell/AppContext'
import type { AppServices } from '../../shell/contracts'
import type { WebService } from '../../types'
import { WebServiceDependencies } from './WebServiceDependencies'
import { RealtimeProvider } from '../../realtime/RealtimeProvider'
import { createRealtimeConnection } from '../../realtime/connection'
import { createRealtimeSession } from '../../realtime/sessionApi'
import { FakeConnection } from '../../test/fakeRealtimeConnection'

vi.mock('../../realtime/connection', () => ({ createRealtimeConnection: vi.fn() }))
vi.mock('../../realtime/sessionApi', () => ({ createRealtimeSession: vi.fn(), revokeRealtimeSession: vi.fn(async () => {}) }))

let root: Root; let container: HTMLDivElement; let cacheClient: QueryClient
function QueryProbe() { const session = useQuerySession(); useLayoutEffect(() => { cacheClient = session.client }); return null }
const requests = strictFetch()
const fetchMock = requests.fetch; const dirty = vi.fn(); const changed = vi.fn()
const service = { id: 'app', name: 'Application', resourceGroupId: 'group', isolationScopeId: 'scope', activeRevision: 7, tags: ['preserved'], exposure: { mode: 'Private' },
  spec: { image: { server: 'registry', name: 'app', tag: 'v1' }, config: { environmentVariables: { LITERAL: 'kept' }, portMappings: [] }, probes: {}, autoscaling: null },
  deployments: [{ deploymentId: 'deployment', deploymentTargetId: 'target', isolationScopeId: 'scope', status: 'Applied', desiredOperation: 'Apply' }] } as unknown as WebService
const connection = { id: 'connection', name: 'Orders network', sourceResourceType: 'pole/web-service', sourceResourceId: 'app', destinationResourceType: 'example/queue', destinationResourceId: 'destination', endpointId: 'orders', status: 'Failed', generation: 2,
  targets: [{ targetId: 'target', phase: 'Apply', status: 'Applied', host: 'orders.first.svc.cluster.local', port: 5672, failureCode: null }, { targetId: 'other', phase: 'Apply', status: 'Failed', host: 'orders.second.svc.cluster.local', port: 5672, failureCode: 'PolicyApplyFailed' }] }
const secret = { id: 'secret', name: 'Application key', resourceGroupId: 'group', isolationScopeId: 'scope', revision: 2, currentVersion: 1, keyNames: ['token'] }
let connectionItems: unknown[]; let writesFail: boolean; let endpointLabel: string; let currentSecret: typeof secret; let liveConnection: FakeConnection
const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status })
beforeEach(() => {
  requests.reset()
  HTMLDialogElement.prototype.showModal = function () { this.setAttribute('open', '') }
  HTMLDialogElement.prototype.close = function () { this.removeAttribute('open') }
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true }); ({ container, root } = createTestApp())
  dirty.mockReset(); changed.mockReset(); fetchMock.mockReset(); connectionItems = [connection]; writesFail = false; endpointLabel = 'Virtual host'; currentSecret = { ...secret }
  vi.mocked(createRealtimeConnection).mockImplementation(() => { liveConnection = new FakeConnection(); return liveConnection })
  vi.mocked(createRealtimeSession).mockImplementation(async () => ({ expiresAtUtc: new Date(Date.now() + 300000).toISOString() }))
  fetchMock.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input)); const path = url.pathname
    if (init?.method && init.method !== 'GET' && (path.startsWith('/api/v1/resource-connections/') || path.startsWith('/api/v1/secrets/') || path === '/api/v1/web-services/app')) return writesFail ? json({ code: 'web-services.dependency-unavailable' }, 409) : json({ id: 'accepted', resourceRevision: 8 }, 202)
    if (path === '/api/v1/resource-connections/endpoints' && url.searchParams.get('resourceId') === 'unavailable') return json({}, 503)
    if (path === '/api/v1/resource-connections/endpoints') return json({ resourceType: 'example/queue', resourceId: 'destination', displayName: 'Queue', availability: 'Supported', topology: { status: 'Eligible', reasonCode: null }, endpoints: [{ id: 'orders', displayName: 'Orders', protocol: 'amqp', transport: 'TCP', port: 5672, configuration: [{ key: 'virtualHost', displayName: endpointLabel, value: '/orders' }] }] })
    if (path === '/api/v1/resource-connections/') return json({ items: connectionItems, totalCount: connectionItems.length })
    if (path === '/api/v1/resource-groups') return json({ items: [{ id: 'group', name: 'Applications' }], totalCount: 1 })
    if (path === '/api/v1/deployment-targets/available') return json([{ id: 'target', displayName: 'Primary cluster' }, { id: 'other', displayName: 'Recovery cluster' }])
    if (path.endsWith('/resources')) return json({ items: [{ registrationId: 'registration', resourceTypeId: 'example/queue', resourceId: 'destination', displayName: 'Orders queue' }], totalCount: 1 })
    if (path === '/api/v1/secrets/') return json({ items: [currentSecret], nextCursor: null })
    if (path.endsWith('/usage')) return json([{ binding: { id: 'binding', consumerType: 'pole/web-service', consumerId: 'app', allDeployments: true, deploymentIds: [], keyMappings: { token: 'APP_TOKEN' } }, affectedDeploymentIds: ['deployment'] }])
    if (path.endsWith('/projections')) return json([{ bindingId: 'binding', resourceDeploymentId: 'deployment', deploymentTargetId: 'target', desiredVersion: 1, appliedVersion: 1, health: 'Ready', failureCode: null }])
    return requests.unhandled(input, init)
  }); vi.stubGlobal('fetch', fetchMock)
})
afterEach(async () => { await act(async () => root.unmount()); container.remove(); requests.verify(); vi.unstubAllGlobals(); vi.useRealTimers() })
async function render(value = service, canWrite = true, tenant = 'alpha', active = true, live = false) {
  const content = <AppContext.Provider value={{ api: { baseUrl: 'http://localhost', token: 'bearer', tenantId: tenant }, navigation: { openResource: vi.fn(), resourceHref: () => '/owned-resource' } } as unknown as AppServices}>
    <QueryProbe /><WebServiceDependencies key={tenant} service={value} canWrite={canWrite} active={active} onDirtyChange={dirty} onChanged={changed} />
  </AppContext.Provider>
  await act(async () => root.render(live ? <RealtimeProvider baseUrl="http://localhost" token="bearer" enabled userId="user">{content}</RealtimeProvider> : content))
}
const button = (name: string) => [...container.querySelectorAll<HTMLButtonElement>('button')].find((item) => item.textContent === name)!
const controls = (label: string) => [...container.querySelectorAll<HTMLLabelElement>('label')].filter((item) => item.querySelector('.field__label')?.textContent === label).map((item) => item.querySelector('input,select') as HTMLInputElement | HTMLSelectElement)
async function input(control: HTMLInputElement | HTMLSelectElement, value: string) {
  await act(async () => { Object.getOwnPropertyDescriptor(control.tagName === 'SELECT' ? HTMLSelectElement.prototype : HTMLInputElement.prototype, 'value')!.set!.call(control, value); control.dispatchEvent(new Event(control.tagName === 'SELECT' ? 'change' : 'input', { bubbles: true })) })
}
async function click(name: string) { await act(async () => button(name).click()) }
async function configurationDraft() {
  await click('Add connection reference'); await input(controls('Environment name')[0], 'ORDER_HOST'); await input(controls('Saved network connection')[0], 'connection')
  await click('Add Secret reference'); await input(controls('Environment name')[1], 'APPLICATION_TOKEN'); await input(controls('Saved Secret')[0], 'secret'); await input(controls('Source Secret key')[0], 'token')
}
it('reconstructs independent persisted steps and keeps failed targets beside successful targets', async () => {
  await render()
  expect(container.textContent).toContain('Orders network'); expect(container.textContent).toContain('PolicyApplyFailed'); expect(container.textContent).toContain('Applied')
  expect(container.textContent).toContain('Primary cluster'); expect(container.textContent).toContain('Recovery cluster')
  expect(container.textContent).toContain('Saved application bindings:'); await click('Application key')
  expect(container.textContent).toContain('Projected to target'); expect(container.textContent).toContain('No dependency references configured')
  expect(fetchMock.mock.calls.filter(([, init]) => init?.method && init.method !== 'GET')).toHaveLength(0)
  const [url, init] = fetchMock.mock.calls.find(([url]) => new URL(String(url)).pathname === '/api/v1/resource-connections/')!
  expect(new URL(String(url)).searchParams.get('sourceResourceId')).toBe('app'); expect(new Headers(init?.headers).get('X-Pole-Tenant-ID')).toBe('alpha')
})
it('creates a custom provider declaration, then prevents duplicate declarations after refresh', async () => {
  connectionItems = []; await render(); await input(controls('Destination resource')[0], 'registration'); await input(controls('Connection endpoint')[0], 'orders'); await input(controls('Connection name')[0], 'Orders network')
  connectionItems = [connection]; await click('Create network access')
  const write = fetchMock.mock.calls.find(([, init]) => init?.method === 'POST')!
  expect(JSON.parse(String(write[1]?.body))).toMatchObject({ destinationResourceType: 'example/queue', endpointId: 'orders', sourceResourceId: 'app' })
  await input(controls('Destination resource')[0], 'registration'); await input(controls('Connection endpoint')[0], 'orders')
  expect(button('Create network access').disabled).toBe(true); expect(container.textContent).toContain('already exists')
})
it('applies explicitly chosen references with current revision and preserves literal values and tags', async () => {
  await render(); await configurationDraft(); await click('Apply dependency configuration')
  expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'PUT')).toBe(false)
  await click('Apply configuration')
  const write = fetchMock.mock.calls.find(([, init]) => init?.method === 'PUT')!; const body = JSON.parse(String(write[1]?.body))
  expect(body.expectedRevision).toBe(7); expect(body.tags).toEqual(['preserved']); expect(body.spec.config.environmentVariables).toEqual({ LITERAL: 'kept' })
  expect(body.spec.config.connectionEnvironmentReferences.ORDER_HOST).toEqual({ connectionId: 'connection', valueKind: 'Host', configurationKey: null })
  expect(body.spec.config.secretEnvironmentReferences.APPLICATION_TOKEN).toEqual({ secretId: 'secret', key: 'token' }); expect(write[1]?.signal).toBeInstanceOf(AbortSignal)
  expect(changed).toHaveBeenCalledOnce(); expect(container.textContent).toContain('Configuration was accepted')
})
it('keeps a rejected draft across refresh and requires explicit review before adopting a newer revision', async () => {
  await render(); await configurationDraft(); writesFail = true; await click('Apply dependency configuration'); await click('Apply configuration')
  await render({ ...service, activeRevision: 8 }); expect(controls('Environment name')[0].value).toBe('ORDER_HOST'); expect(container.textContent).toContain('draft remains')
  await click('Review current references'); await click('Keep draft for this revision'); await click('Apply dependency configuration'); await click('Apply configuration')
  const writes = fetchMock.mock.calls.filter(([, init]) => init?.method === 'PUT'); expect(JSON.parse(String(writes.at(-1)![1]?.body)).expectedRevision).toBe(8)
})
it('viewer access cannot mutate or start configuration drafts', async () => {
  await render(service, false)
  expect(button('Create network access')).toBeUndefined(); expect(button('Add Secret reference')).toBeUndefined(); expect(button('Disconnect network access')).toBeUndefined()
  expect(container.textContent).toContain('Tenant viewer access is read-only')
})
it('retains usable provider fields when another connection provider is unavailable', async () => {
  connectionItems = [connection, { ...connection, id: 'other-connection', destinationResourceId: 'unavailable', name: 'Unavailable dependency' }]
  await render(); expect(container.textContent).toContain('Some provider configuration fields are unavailable')
  await click('Add connection reference'); await input(controls('Environment name')[0], 'ORDERS_NAMESPACE'); await input(controls('Saved network connection')[0], 'connection')
  await input(controls('Connection field')[0], 'Configuration'); await input(controls('Provider configuration field')[0], 'virtualHost')
  await click('Apply dependency configuration'); await click('Apply configuration')
  const write = fetchMock.mock.calls.find(([, init]) => init?.method === 'PUT')!
  expect(JSON.parse(String(write[1]?.body)).spec.config.connectionEnvironmentReferences.ORDERS_NAMESPACE).toEqual({ connectionId: 'connection', valueKind: 'Configuration', configurationKey: 'virtualHost' })
})

it('keeps the selected Secret, binding and projections through a slow failed metadata refresh', async () => {
  await render(); await click('Application key')
  const choice = controls('Dependency Secret')[0]
  expect(choice.value).toBe('secret')
  const read = deferred<Response>()
  const baseline = fetchMock.getMockImplementation()!
  fetchMock.mockImplementation((input, init) => new URL(String(input)).pathname === '/api/v1/secrets/' ? read.promise : baseline(input, init))
  await click('Refresh dependencies')
  expect(controls('Dependency Secret')[0].value).toBe('secret')
  expect(container.textContent).toContain('saved binding for every current deployment')
  expect(container.textContent).toContain('Projected to target')
  expect(button('Detach application binding').disabled).toBe(true)
  await act(async () => read.resolve(new Response('private-canary', { status: 503 })))
  expect(controls('Dependency Secret')[0].value).toBe('secret')
  expect(container.textContent).toContain('saved binding for every current deployment')
  expect(container.textContent).toContain('Projected to target')
  expect(container.textContent).not.toContain('private-canary')
  expect(button('Detach application binding').disabled).toBe(true)
})

it('retains endpoint choices but blocks network writes while discovery refresh is pending or failed', async () => {
  connectionItems = []
  await render(); await input(controls('Destination resource')[0], 'registration'); await input(controls('Connection endpoint')[0], 'orders'); await input(controls('Connection name')[0], 'Orders')
  expect(button('Create network access').disabled).toBe(false)
  const read = deferred<Response>()
  const baseline = fetchMock.getMockImplementation()!
  fetchMock.mockImplementation((input, init) => new URL(String(input)).pathname === '/api/v1/resource-connections/endpoints' ? read.promise : baseline(input, init))
  let refresh!: Promise<void>
  await act(async () => { refresh = cacheClient.refetchQueries({ predicate: (query) => query.queryKey.includes('endpoints') }) })
  expect(controls('Connection endpoint')[0].value).toBe('orders')
  expect(button('Create network access').disabled).toBe(true)
  await act(async () => { read.resolve(new Response('Discovery unavailable', { status: 503 })); await refresh })
  expect(controls('Connection endpoint')[0].value).toBe('orders')
  expect(button('Create network access').disabled).toBe(true)
})

it('observes connection fields and composite Secret bindings through hints, preserving drafts and silencing retained hidden panels', async () => {
  vi.useFakeTimers()
  await render(service, true, 'alpha', true, true)
  await act(async () => vi.advanceTimersByTimeAsync(500))
  await click('Add connection reference'); await input(controls('Environment name')[0], 'UNSAVED_HOST'); await input(controls('Saved network connection')[0], 'connection')
  await input(controls('Connection field')[0], 'Configuration'); await input(controls('Provider configuration field')[0], 'virtualHost')
  endpointLabel = 'Committed provider field'; connectionItems = [{ ...connection, name: 'Committed network metadata' }]
  await act(async () => liveConnection.changed({ version: 1, topic: 'resource-connections', tenantId: 'alpha', resourceId: 'connection' }))
  await act(async () => vi.advanceTimersByTimeAsync(250))
  expect(controls('Environment name')[0].value).toBe('UNSAVED_HOST')
  expect(controls('Provider configuration field')[0].textContent).toContain('Committed provider field')
  expect(container.textContent).toContain('Committed network metadata')
  currentSecret = { ...currentSecret, name: 'Committed Secret metadata' }
  await act(async () => liveConnection.changed({ version: 1, topic: 'secrets', tenantId: 'alpha', resourceId: 'secret' }))
  await act(async () => vi.advanceTimersByTimeAsync(250))
  expect(container.textContent).toContain('Committed Secret metadata')
  expect(controls('Environment name')[0].value).toBe('UNSAVED_HOST')
  expect(fetchMock.mock.calls.some(([, init]) => init?.method && init.method !== 'GET')).toBe(false)
  await render(service, true, 'alpha', false, true)
  fetchMock.mockClear()
  await act(async () => vi.advanceTimersByTimeAsync(120000))
  expect(fetchMock).not.toHaveBeenCalled()
  expect(liveConnection.unsubscribe).toHaveBeenCalled()
})
