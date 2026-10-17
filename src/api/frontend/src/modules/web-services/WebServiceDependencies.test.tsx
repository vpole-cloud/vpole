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
import type { SecretUsage, WebService } from '../../types'
import { WebServiceDependencies } from './WebServiceDependencies'
import { RealtimeProvider } from '../../realtime/RealtimeProvider'
import { createRealtimeConnection } from '../../realtime/connection'
import { createRealtimeSession } from '../../realtime/sessionApi'
import { FakeConnection } from '../../test/fakeRealtimeConnection'

vi.mock('../../realtime/connection', () => ({ createRealtimeConnection: vi.fn() }))
vi.mock('../../realtime/sessionApi', () => ({
  createRealtimeSession: vi.fn(),
  revokeRealtimeSession: vi.fn(async () => {}),
}))

let root: Root
let container: HTMLDivElement
let cacheClient: QueryClient
function QueryProbe() {
  const session = useQuerySession()
  useLayoutEffect(() => {
    cacheClient = session.client
  })
  return null
}
const requests = strictFetch()
const fetchMock = requests.fetch
const dirty = vi.fn()
const changed = vi.fn()
const service = {
  id: 'app',
  name: 'Application',
  resourceGroupId: 'group',
  isolationScopeId: 'scope',
  activeRevision: 7,
  tags: ['preserved'],
  exposure: { mode: 'Private' },
  spec: {
    image: { server: 'registry', name: 'app', tag: 'v1' },
    config: { environmentVariables: { LITERAL: 'kept' }, portMappings: [] },
    probes: {},
    autoscaling: null,
  },
  deployments: [
    {
      deploymentId: 'deployment',
      deploymentTargetId: 'target',
      isolationScopeId: 'scope',
      status: 'Applied',
      desiredOperation: 'Apply',
    },
  ],
} as unknown as WebService
const connection = {
  id: 'connection',
  name: 'Orders network',
  sourceResourceType: 'pole/web-service',
  sourceResourceId: 'app',
  destinationResourceType: 'example/queue',
  destinationResourceId: 'destination',
  endpointId: 'orders',
  status: 'Failed',
  generation: 2,
  targets: [
    {
      targetId: 'target',
      phase: 'Apply',
      status: 'Applied',
      host: 'orders.first.svc.cluster.local',
      port: 5672,
      failureCode: null,
    },
    {
      targetId: 'other',
      phase: 'Apply',
      status: 'Failed',
      host: 'orders.second.svc.cluster.local',
      port: 5672,
      failureCode: 'PolicyApplyFailed',
    },
  ],
}
const secret = {
  id: 'secret',
  name: 'Application key',
  resourceGroupId: 'group',
  isolationScopeId: 'scope',
  revision: 2,
  currentVersion: 1,
  keyNames: ['token'],
}
let connectionItems: unknown[]
let writesFail: boolean
let endpointLabel: string
let currentSecret: typeof secret & { managedBy?: { bindingId: string } }
let liveConnection: FakeConnection
let usageItems: SecretUsage[]
let heldChoices: Promise<Response> | undefined
let heldUsage: Promise<Response> | undefined
let choicesFail: boolean
let usageFail: boolean
const initialUsage = () => [
  {
    binding: {
      id: 'binding',
      consumerType: 'pole/web-service',
      consumerId: 'app',
      allDeployments: true,
      deploymentIds: [],
      keyMappings: { token: 'APP_TOKEN' },
    },
    affectedDeploymentIds: ['deployment'],
  },
]
const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status })
beforeEach(() => {
  requests.reset()
  HTMLDialogElement.prototype.showModal = function () {
    this.setAttribute('open', '')
  }
  HTMLDialogElement.prototype.close = function () {
    this.removeAttribute('open')
  }
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  ;({ container, root } = createTestApp())
  dirty.mockReset()
  changed.mockReset()
  fetchMock.mockReset()
  connectionItems = [connection]
  writesFail = false
  endpointLabel = 'Virtual host'
  currentSecret = { ...secret }
  usageItems = initialUsage()
  heldChoices = undefined
  heldUsage = undefined
  choicesFail = false
  usageFail = false
  vi.mocked(createRealtimeConnection).mockImplementation(() => {
    liveConnection = new FakeConnection()
    return liveConnection
  })
  vi.mocked(createRealtimeSession).mockImplementation(async () => ({
    expiresAtUtc: new Date(Date.now() + 300000).toISOString(),
  }))
  fetchMock.mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input))
    const path = url.pathname
    if (
      init?.method &&
      init.method !== 'GET' &&
      (path.startsWith('/api/v1/resource-connections/') ||
        path.startsWith('/api/v1/secrets/') ||
        path === '/api/v1/web-services/app')
    )
      return writesFail
        ? json({ code: 'web-services.dependency-unavailable' }, 409)
        : json({ id: 'accepted', resourceRevision: 8 }, 202)
    if (
      path === '/api/v1/resource-connections/endpoints' &&
      url.searchParams.get('resourceId') === 'unavailable'
    )
      return json({}, 503)
    if (path === '/api/v1/resource-connections/endpoints')
      return json({
        resourceType: 'example/queue',
        resourceId: 'destination',
        displayName: 'Queue',
        availability: 'Supported',
        topology: { status: 'Eligible', reasonCode: null },
        endpoints: [
          {
            id: 'orders',
            displayName: 'Orders',
            protocol: 'amqp',
            transport: 'TCP',
            port: 5672,
            configuration: [{ key: 'virtualHost', displayName: endpointLabel, value: '/orders' }],
          },
        ],
      })
    if (path === '/api/v1/resource-connections/')
      return json({ items: connectionItems, totalCount: connectionItems.length })
    if (path === '/api/v1/resource-groups')
      return json({ items: [{ id: 'group', name: 'Applications' }], totalCount: 1 })
    if (path === '/api/v1/deployment-targets/available')
      return json([
        { id: 'target', displayName: 'Primary cluster' },
        { id: 'other', displayName: 'Recovery cluster' },
      ])
    if (path.endsWith('/resources'))
      return json({
        items: [
          {
            registrationId: 'registration',
            resourceTypeId: 'example/queue',
            resourceId: 'destination',
            displayName: 'Orders queue',
          },
        ],
        totalCount: 1,
      })
    if (path === '/api/v1/secrets/')
      return (
        heldChoices?.then((response) => response.clone()) ??
        (choicesFail ? json({}, 503) : json({ items: [currentSecret], nextCursor: null }))
      )
    if (path.endsWith('/usage'))
      return (
        heldUsage?.then((response) => response.clone()) ??
        (usageFail ? json({}, 503) : json(usageItems))
      )
    if (path.endsWith('/projections'))
      return json([
        {
          bindingId: 'binding',
          resourceDeploymentId: 'deployment',
          deploymentTargetId: 'target',
          desiredVersion: 1,
          appliedVersion: 1,
          health: 'Ready',
          failureCode: null,
        },
      ])
    return requests.unhandled(input, init)
  })
  vi.stubGlobal('fetch', fetchMock)
})
afterEach(async () => {
  await act(async () => root.unmount())
  container.remove()
  requests.verify()
  vi.unstubAllGlobals()
  vi.useRealTimers()
})
async function render(
  value = service,
  canWrite = true,
  tenant = 'alpha',
  active = true,
  live = false,
) {
  const content = (
    <AppContext.Provider
      value={
        {
          api: { baseUrl: 'http://localhost', token: 'bearer', tenantId: tenant },
          navigation: { openResource: vi.fn(), resourceHref: () => '/owned-resource' },
        } as unknown as AppServices
      }
    >
      <QueryProbe />
      <WebServiceDependencies
        key={tenant}
        service={value}
        canWrite={canWrite}
        active={active}
        onDirtyChange={dirty}
        onChanged={changed}
      />
    </AppContext.Provider>
  )
  await act(async () =>
    root.render(
      live ? (
        <RealtimeProvider baseUrl="http://localhost" token="bearer" enabled userId="user">
          {content}
        </RealtimeProvider>
      ) : (
        content
      ),
    ),
  )
}
const button = (name: string) =>
  [...container.querySelectorAll<HTMLButtonElement>('button')].find(
    (item) => item.textContent === name,
  )!
const controls = (label: string) =>
  [...container.querySelectorAll<HTMLLabelElement>('label')]
    .filter((item) => item.querySelector('.field__label')?.textContent === label)
    .map((item) => item.querySelector('input,select') as HTMLInputElement | HTMLSelectElement)
async function input(control: HTMLInputElement | HTMLSelectElement, value: string) {
  await act(async () => {
    Object.getOwnPropertyDescriptor(
      control.tagName === 'SELECT' ? HTMLSelectElement.prototype : HTMLInputElement.prototype,
      'value',
    )!.set!.call(control, value)
    control.dispatchEvent(
      new Event(control.tagName === 'SELECT' ? 'change' : 'input', { bubbles: true }),
    )
  })
}
async function click(name: string) {
  await act(async () => button(name).click())
}
async function configurationDraft() {
  await click('Add connection reference')
  await input(controls('Environment name')[0], 'ORDER_HOST')
  await input(controls('Saved network connection')[0], 'connection')
  await click('Add Secret reference')
  await input(controls('Environment name')[1], 'APPLICATION_TOKEN')
  await input(controls('Saved Secret')[0], 'secret')
  await input(controls('Source Secret key')[0], 'token')
}
it('reconstructs independent persisted steps and keeps failed targets beside successful targets', async () => {
  await render()
  expect(container.textContent).toContain('Orders network')
  expect(container.textContent).toContain('PolicyApplyFailed')
  expect(container.textContent).toContain('Applied')
  expect(container.textContent).toContain('Primary cluster')
  expect(container.textContent).toContain('Recovery cluster')
  expect(container.textContent).toContain('Saved application bindings:')
  await click('Application key')
  expect(container.textContent).toContain('Projected to target')
  expect(container.textContent).toContain('No dependency references configured')
  expect(
    fetchMock.mock.calls.filter(([, init]) => init?.method && init.method !== 'GET'),
  ).toHaveLength(0)
  const [url, init] = fetchMock.mock.calls.find(
    ([url]) => new URL(String(url)).pathname === '/api/v1/resource-connections/',
  )!
  expect(new URL(String(url)).searchParams.get('sourceResourceId')).toBe('app')
  expect(new Headers(init?.headers).get('X-Pole-Tenant-ID')).toBe('alpha')
})
it('creates a custom provider declaration, then prevents duplicate declarations after refresh', async () => {
  connectionItems = []
  await render()
  await input(controls('Destination resource')[0], 'registration')
  await input(controls('Connection endpoint')[0], 'orders')
  await input(controls('Connection name')[0], 'Orders network')
  connectionItems = [connection]
  await click('Create network access')
  const write = fetchMock.mock.calls.find(([, init]) => init?.method === 'POST')!
  expect(JSON.parse(String(write[1]?.body))).toMatchObject({
    destinationResourceType: 'example/queue',
    endpointId: 'orders',
    sourceResourceId: 'app',
  })
  await input(controls('Destination resource')[0], 'registration')
  await input(controls('Connection endpoint')[0], 'orders')
  expect(button('Create network access').disabled).toBe(true)
  expect(container.textContent).toContain('already exists')
})
it('applies explicitly chosen references with current revision and preserves literal values and tags', async () => {
  await render()
  await configurationDraft()
  await click('Apply dependency configuration')
  expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'PUT')).toBe(false)
  await click('Apply configuration')
  const write = fetchMock.mock.calls.find(([, init]) => init?.method === 'PUT')!
  const body = JSON.parse(String(write[1]?.body))
  expect(body.expectedRevision).toBe(7)
  expect(body.tags).toEqual(['preserved'])
  expect(body.spec.config.environmentVariables).toEqual({ LITERAL: 'kept' })
  expect(body.spec.config.connectionEnvironmentReferences.ORDER_HOST).toEqual({
    connectionId: 'connection',
    valueKind: 'Host',
    configurationKey: null,
  })
  expect(body.spec.config.secretEnvironmentReferences.APPLICATION_TOKEN).toEqual({
    secretId: 'secret',
    key: 'token',
  })
  expect(write[1]?.signal).toBeInstanceOf(AbortSignal)
  expect(changed).toHaveBeenCalledOnce()
  expect(container.textContent).toContain('Configuration was accepted')
})
it('keeps a rejected draft across refresh and requires explicit review before adopting a newer revision', async () => {
  await render()
  await configurationDraft()
  writesFail = true
  await click('Apply dependency configuration')
  await click('Apply configuration')
  await render({ ...service, activeRevision: 8 })
  expect(controls('Environment name')[0].value).toBe('ORDER_HOST')
  expect(container.textContent).toContain('draft remains')
  await click('Review current references')
  await click('Keep draft for this revision')
  await click('Apply dependency configuration')
  await click('Apply configuration')
  const writes = fetchMock.mock.calls.filter(([, init]) => init?.method === 'PUT')
  expect(JSON.parse(String(writes.at(-1)![1]?.body)).expectedRevision).toBe(8)
})
it('viewer access cannot mutate or start configuration drafts', async () => {
  await render(service, false)
  expect(button('Create network access')).toBeUndefined()
  expect(button('Add Secret reference')).toBeUndefined()
  expect(button('Disconnect network access')).toBeUndefined()
  expect(container.textContent).toContain('Tenant viewer access is read-only')
})
it('retains usable provider fields when another connection provider is unavailable', async () => {
  connectionItems = [
    connection,
    {
      ...connection,
      id: 'other-connection',
      destinationResourceId: 'unavailable',
      name: 'Unavailable dependency',
    },
  ]
  await render()
  expect(container.textContent).toContain('Some provider configuration fields are unavailable')
  await click('Add connection reference')
  await input(controls('Environment name')[0], 'ORDERS_NAMESPACE')
  await input(controls('Saved network connection')[0], 'connection')
  await input(controls('Connection field')[0], 'Configuration')
  await input(controls('Provider configuration field')[0], 'virtualHost')
  await click('Apply dependency configuration')
  await click('Apply configuration')
  const write = fetchMock.mock.calls.find(([, init]) => init?.method === 'PUT')!
  expect(
    JSON.parse(String(write[1]?.body)).spec.config.connectionEnvironmentReferences.ORDERS_NAMESPACE,
  ).toEqual({
    connectionId: 'connection',
    valueKind: 'Configuration',
    configurationKey: 'virtualHost',
  })
})

it('keeps the selected Secret, binding and projections through a slow failed metadata refresh', async () => {
  await render()
  await click('Application key')
  const choice = controls('Dependency Secret')[0]
  expect(choice.value).toBe('secret')
  const read = deferred<Response>()
  const baseline = fetchMock.getMockImplementation()!
  fetchMock.mockImplementation((input, init) =>
    new URL(String(input)).pathname === '/api/v1/secrets/' ? read.promise : baseline(input, init),
  )
  await click('Refresh dependencies')
  expect(controls('Dependency Secret')[0].value).toBe('secret')
  expect(container.textContent).toContain('saved binding for every current deployment')
  expect(container.textContent).toContain('Projected to target')
  expect(button('Detach application binding').disabled).toBe(false)
  await act(async () => read.resolve(new Response('private-canary', { status: 503 })))
  expect(controls('Dependency Secret')[0].value).toBe('secret')
  expect(container.textContent).toContain('saved binding for every current deployment')
  expect(container.textContent).toContain('Projected to target')
  expect(container.textContent).not.toContain('private-canary')
  expect(button('Detach application binding').disabled).toBe(true)
})

it('retains endpoint choices but blocks network writes while discovery refresh is pending or failed', async () => {
  connectionItems = []
  await render()
  await input(controls('Destination resource')[0], 'registration')
  await input(controls('Connection endpoint')[0], 'orders')
  await input(controls('Connection name')[0], 'Orders')
  expect(button('Create network access').disabled).toBe(false)
  const read = deferred<Response>()
  const baseline = fetchMock.getMockImplementation()!
  fetchMock.mockImplementation((input, init) =>
    new URL(String(input)).pathname === '/api/v1/resource-connections/endpoints'
      ? read.promise
      : baseline(input, init),
  )
  let refresh!: Promise<void>
  await act(async () => {
    refresh = cacheClient.refetchQueries({
      predicate: (query) => query.queryKey.includes('endpoints'),
    })
  })
  expect(controls('Connection endpoint')[0].value).toBe('orders')
  expect(button('Create network access').disabled).toBe(true)
  await act(async () => {
    read.resolve(new Response('Discovery unavailable', { status: 503 }))
    await refresh
  })
  expect(controls('Connection endpoint')[0].value).toBe('orders')
  expect(button('Create network access').disabled).toBe(true)
})

it('observes connection fields and composite Secret bindings through hints, preserving drafts and silencing retained hidden panels', async () => {
  vi.useFakeTimers()
  await render(service, true, 'alpha', true, true)
  await act(async () => vi.advanceTimersByTimeAsync(500))
  await click('Add connection reference')
  await input(controls('Environment name')[0], 'UNSAVED_HOST')
  await input(controls('Saved network connection')[0], 'connection')
  await input(controls('Connection field')[0], 'Configuration')
  await input(controls('Provider configuration field')[0], 'virtualHost')
  endpointLabel = 'Committed provider field'
  connectionItems = [{ ...connection, name: 'Committed network metadata' }]
  await act(async () =>
    liveConnection.changed({
      version: 1,
      topic: 'resource-connections',
      tenantId: 'alpha',
      resourceId: 'connection',
    }),
  )
  await act(async () => vi.advanceTimersByTimeAsync(250))
  expect(controls('Environment name')[0].value).toBe('UNSAVED_HOST')
  expect(controls('Provider configuration field')[0].textContent).toContain(
    'Committed provider field',
  )
  expect(container.textContent).toContain('Committed network metadata')
  currentSecret = { ...currentSecret, name: 'Committed Secret metadata' }
  await act(async () =>
    liveConnection.changed({
      version: 1,
      topic: 'secrets',
      tenantId: 'alpha',
      resourceId: 'secret',
    }),
  )
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

const secretWrites = () =>
  fetchMock.mock.calls.filter(
    ([url, init]) =>
      String(url).includes('/api/v1/secrets/') && init?.method && init.method !== 'GET',
  )
async function selectedSecret(bound = true) {
  vi.useFakeTimers()
  if (!bound) usageItems = []
  await render(service, true, 'alpha', true, true)
  await act(async () => vi.advanceTimersByTimeAsync(500))
  await input(controls('Dependency Secret')[0], 'secret')
}
async function secretHint() {
  await act(async () =>
    liveConnection.changed({
      version: 1,
      topic: 'secrets',
      tenantId: 'alpha',
      resourceId: 'secret',
    }),
  )
  await act(async () => vi.advanceTimersByTimeAsync(250))
}
function detachConfirm() {
  return button('Detach binding')
}

it.each(['choices', 'usage', 'both'] as const)(
  'authorizes the selected eligible Secret from loaded empty usage during held %s push reads',
  async (heldRead) => {
    await selectedSecret(false)
    const choicesRead = deferred<Response>()
    const usageRead = deferred<Response>()
    if (heldRead !== 'usage') heldChoices = choicesRead.promise
    if (heldRead !== 'choices') heldUsage = usageRead.promise
    await secretHint()
    expect.soft(button('Authorize Secret for all application deployments').disabled).toBe(false)
    await click('Authorize Secret for all application deployments')
    expect(secretWrites()).toHaveLength(1)
    const [url, command] = secretWrites()[0]
    expect(String(url)).toBe('http://localhost/api/v1/secrets/secret/bindings')
    expect(JSON.parse(command?.body as string)).toEqual({
      consumerType: 'pole/web-service',
      consumerId: 'app',
      allDeployments: true,
      deploymentIds: [],
      keyMappings: {},
    })
    expect(new Headers(command?.headers).get('X-Pole-Tenant-ID')).toBe('alpha')
    expect(new Headers(command?.headers).get('Authorization')).toBe('Bearer bearer')
    heldChoices = undefined
    heldUsage = undefined
    await act(async () => {
      choicesRead.resolve(json({ items: [currentSecret], nextCursor: null }))
      usageRead.resolve(json(usageItems))
    })
  },
)

it('opens and confirms exact application binding removal during healthy held choices and usage reads', async () => {
  await selectedSecret()
  const choicesRead = deferred<Response>()
  const usageRead = deferred<Response>()
  heldChoices = choicesRead.promise
  heldUsage = usageRead.promise
  await secretHint()
  expect.soft(button('Detach application binding').disabled).toBe(false)
  await click('Detach application binding')
  expect(container.querySelector('dialog[open]')).not.toBeNull()
  expect(detachConfirm().disabled).toBe(false)
  await click('Detach binding')
  expect(secretWrites()).toHaveLength(1)
  const [url, command] = secretWrites()[0]
  expect(String(url)).toBe('http://localhost/api/v1/secrets/secret/bindings/binding')
  expect(command?.method).toBe('DELETE')
  expect(new Headers(command?.headers).get('X-Pole-Tenant-ID')).toBe('alpha')
  heldChoices = undefined
  heldUsage = undefined
  await act(async () => {
    choicesRead.resolve(json({ items: [currentSecret], nextCursor: null }))
    usageRead.resolve(json(usageItems))
  })
})

it.each(['choices', 'usage'] as const)(
  'blocks an open Secret detach after failed %s metadata and through a held retry',
  async (failedRead) => {
    await selectedSecret()
    await click('Detach application binding')
    choicesFail = failedRead === 'choices'
    usageFail = failedRead === 'usage'
    await secretHint()
    expect.soft(detachConfirm().disabled).toBe(true)
    await click('Detach binding')
    expect(secretWrites()).toHaveLength(0)
    choicesFail = false
    usageFail = false
    const read = deferred<Response>()
    if (failedRead === 'choices') heldChoices = read.promise
    else heldUsage = read.promise
    await secretHint()
    expect(detachConfirm().disabled).toBe(true)
    await click('Detach binding')
    expect(secretWrites()).toHaveLength(0)
    heldChoices = undefined
    heldUsage = undefined
    await act(async () =>
      read.resolve(
        json(failedRead === 'choices' ? { items: [currentSecret], nextCursor: null } : usageItems),
      ),
    )
    expect(detachConfirm().disabled).toBe(false)
  },
)

it.each([
  'binding',
  'secret revision',
  'source revision',
  'managed binding',
  'group',
  'scope',
  'selection',
  'removed binding',
  'removed Secret',
] as const)(
  'does not retarget an open Secret detach after current %s changes',
  async (changedField) => {
    await selectedSecret()
    await click('Detach application binding')
    if (changedField === 'binding')
      usageItems = [{ ...usageItems[0], binding: { ...usageItems[0].binding, id: 'new-binding' } }]
    if (changedField === 'secret revision') currentSecret = { ...currentSecret, revision: 3 }
    if (changedField === 'managed binding')
      currentSecret = { ...currentSecret, managedBy: { bindingId: 'binding' } }
    if (changedField === 'group')
      currentSecret = { ...currentSecret, resourceGroupId: 'other-group' }
    if (changedField === 'scope')
      currentSecret = { ...currentSecret, isolationScopeId: 'other-scope' }
    if (changedField === 'removed binding') usageItems = []
    if (changedField === 'selection') await input(controls('Dependency Secret')[0], '')
    if (changedField === 'removed Secret') {
      const read = deferred<Response>()
      heldChoices = read.promise
      await secretHint()
      heldChoices = undefined
      await act(async () => read.resolve(json({ items: [], nextCursor: null })))
    } else if (changedField === 'source revision')
      await render({ ...service, activeRevision: 8 }, true, 'alpha', true, true)
    else await secretHint()
    expect.soft(detachConfirm()?.disabled ?? true).toBe(true)
    if (detachConfirm()) await click('Detach binding')
    expect(secretWrites()).toHaveLength(0)
  },
)

it('requires initial usage and current permission/scope for Secret commands', async () => {
  usageItems = []
  const read = deferred<Response>()
  heldUsage = read.promise
  await render()
  await input(controls('Dependency Secret')[0], 'secret')
  expect(button('Authorize Secret for all application deployments').disabled).toBe(true)
  await click('Authorize Secret for all application deployments')
  expect(secretWrites()).toHaveLength(0)
  heldUsage = undefined
  await act(async () => read.resolve(json(initialUsage())))
  await click('Detach application binding')
  await render(service, false)
  expect(detachConfirm()).toBeUndefined()
  expect(secretWrites()).toHaveLength(0)
  await render(service, true, 'beta')
  expect(container.querySelector('dialog[open]')).toBeNull()
  expect(secretWrites()).toHaveLength(0)
})

it.each(['choices', 'usage'] as const)(
  'does not authorize a Secret after failed %s validation or during its held retry',
  async (failedRead) => {
    await selectedSecret(false)
    choicesFail = failedRead === 'choices'
    usageFail = failedRead === 'usage'
    await secretHint()
    expect(button('Authorize Secret for all application deployments').disabled).toBe(true)
    await click('Authorize Secret for all application deployments')
    expect(secretWrites()).toHaveLength(0)
    choicesFail = false
    usageFail = false
    const read = deferred<Response>()
    if (failedRead === 'choices') heldChoices = read.promise
    else heldUsage = read.promise
    await secretHint()
    expect(button('Authorize Secret for all application deployments').disabled).toBe(true)
    await click('Authorize Secret for all application deployments')
    expect(secretWrites()).toHaveLength(0)
    heldChoices = undefined
    heldUsage = undefined
    await act(async () =>
      read.resolve(
        json(failedRead === 'choices' ? { items: [currentSecret], nextCursor: null } : usageItems),
      ),
    )
    expect(button('Authorize Secret for all application deployments').disabled).toBe(false)
  },
)

it('keeps authorization eligibility distinct from removing an existing invalid-scope binding', async () => {
  currentSecret = { ...currentSecret, isolationScopeId: 'other-scope' }
  usageItems = []
  await render()
  await input(controls('Dependency Secret')[0], 'secret')
  expect(button('Authorize Secret for all application deployments')).toBeUndefined()
  expect(secretWrites()).toHaveLength(0)
  usageItems = initialUsage()
  await click('Refresh Secret access')
  await click('Detach application binding')
  await click('Detach binding')
  expect(secretWrites()).toHaveLength(1)
  expect(secretWrites()[0][1]?.method).toBe('DELETE')
})
