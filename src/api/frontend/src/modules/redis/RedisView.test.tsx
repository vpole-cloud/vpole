// @vitest-environment jsdom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { strictFetch } from '../../test/http'
import { AppShell } from '../../shell/AppShell'
import { createAppRegistry } from '../../shell/moduleRegistry'
import type { RedisService } from '../../types'
import { redisModule } from './RedisModule'
import { overviewModule } from '../platform/PlatformModules'
import { RedisView } from './RedisView'
import { OverviewView } from '../platform/PlatformViews'

const registry = createAppRegistry([
  { ...redisModule, views: redisModule.views.map((view) => ({ ...view, component: RedisView })) },
  {
    ...overviewModule,
    views: overviewModule.views.map((view) => ({ ...view, component: OverviewView })),
  },
])
const initial: RedisService = {
  id: 'database',
  name: 'orders',
  resourceGroupId: 'group',
  revision: 1,
  lifecycle: 'Active',
  tags: ['cache'],
  spec: { storage: { sizeGb: 1, storageProfileId: 'disk' } },
  placement: {
    deploymentId: 'deployment',
    isolationScopeId: 'scope',
    runtimeCellId: 'cell',
    deploymentTargetId: 'target',
    desiredGeneration: 1,
    observedGeneration: 1,
    status: 'Applied',
    fixedReplicas: 1,
    resourceRevision: 1,
    deploymentRevision: 1,
    desiredOperation: 'Apply',
    compute: {
      displayName: 'Small',
      profile: { computeProfileId: 'small', computeProfileRevision: 1 },
      requests: { cpuMillis: 100, memoryMi: 128 },
      limits: { cpuMillis: 200, memoryMi: 256 },
      extendedResources: [],
    },
    endpoints: [{ kind: 'Redis', host: 'orders.internal', port: 6379, scheme: 'redis' }],
  },
}
let root: Root
let container: HTMLDivElement
let redis: RedisService
let failUpdate: boolean
let failList: boolean
let pendingDetails: Promise<Response> | undefined
const requests = strictFetch()
const fetchMock = requests.fetch
const json = (value: unknown) => Promise.resolve(new Response(JSON.stringify(value)))
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
  vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => {
    callback(0)
    return 1
  })
  localStorage.clear()
  window.history.replaceState({}, '', '/?view=redis&resource=catalog')
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
  redis = structuredClone(initial)
  failUpdate = false
  failList = false
  pendingDetails = undefined
  requests.reset()
  fetchMock.mockImplementation((input, init) => {
    const url = new URL(String(input), 'http://localhost')
    if (url.pathname.endsWith('/redis-services/')) {
      if (init?.method === 'POST') return json({ redisId: redis.id, placement: redis.placement })
      if (failList) return Promise.resolve(new Response('Catalog unavailable', { status: 503 }))
      return json({
        items: [redis],
        totalCount: 20,
        pageNumber: Number(url.searchParams.get('pageNumber')),
        pageSize: Number(url.searchParams.get('pageSize')),
      })
    }
    if (url.pathname.endsWith('/redis-services/database')) {
      if (init?.method === 'PUT') {
        if (failUpdate)
          return Promise.resolve(
            new Response(
              JSON.stringify({
                title: 'Validation failed',
                errors: { 'Body.Spec.Storage.SizeGb': ['Invalid capacity.'] },
              }),
              { status: 400 },
            ),
          )
        redis = { ...redis, ...JSON.parse(init.body as string), revision: redis.revision + 1 }
      }
      return pendingDetails ?? json(redis)
    }
    if (url.pathname.endsWith('/resource-deployments/operations'))
      return json({
        items: [],
        totalCount: 0,
        pageNumber: 1,
        pageSize: 10,
        checkedAtUtc: new Date().toISOString(),
        counts: { waiting: 0, accepted: 0, running: 0, applied: 0, failed: 0 },
      })
    if (url.pathname.endsWith('/retry')) return json({})
    if (url.pathname.endsWith('/web-services'))
      return json({ items: [], totalCount: 0, pageNumber: 1, pageSize: 8 })
    if (url.pathname.endsWith('/resource-groups'))
      return json({
        items: [{ id: 'group', name: 'Applications', tags: [], description: '', resourceCount: 1 }],
        totalCount: 1,
        pageNumber: 1,
        pageSize: 8,
      })
    if (url.pathname.endsWith('/resource-groups/group'))
      return json({
        id: 'group',
        name: 'Applications',
        tags: [],
        description: '',
        resourceCount: 1,
      })
    if (url.pathname.endsWith('/isolation-scopes'))
      return json([
        {
          id: 'scope',
          resourceGroupId: 'group',
          name: 'Production',
          isDefault: true,
          lifecycle: 'active',
        },
      ])
    if (url.pathname.endsWith('/deployment-targets/available'))
      return json([{ id: 'target', displayName: 'Local cluster' }])
    if (url.pathname.endsWith('/compute-profiles/available'))
      return json([{ id: 'small', displayName: 'Small', isDefault: true }])
    if (url.pathname.endsWith('/storage-profiles'))
      return json([
        {
          id: 'disk',
          displayName: 'Standard disk',
          isDefault: true,
          targetMappings: [{ deploymentTargetId: 'target', storageClassName: 'standard' }],
        },
      ])
    if (url.pathname.endsWith('/resource-deployments/operations'))
      return json({
        items: [],
        totalCount: 0,
        pageNumber: 1,
        pageSize: 10,
        checkedAtUtc: new Date().toISOString(),
        counts: { waiting: 0, accepted: 0, running: 0, applied: 0, failed: 0 },
      })
    if (url.pathname.endsWith('/observation'))
      return json({
        deploymentRevision: redis.placement?.deploymentRevision,
        desiredGeneration: redis.placement?.desiredGeneration,
        checkedAtUtc: new Date().toISOString(),
        observation: { kind: 'Unavailable', reason: 'Unsupported' },
      })
    if (url.pathname.endsWith('/resource-connections/endpoints'))
      return json({
        availability: 'Supported',
        endpoints: [],
        topology: { status: 'NotEvaluated', reasons: [] },
      })
    if (url.pathname.endsWith('/postgres/'))
      return json({ items: [], totalCount: 0, pageNumber: 1, pageSize: 8 })
    return requests.unhandled(input, init)
  })
  vi.stubGlobal('fetch', fetchMock)
})
afterEach(async () => {
  await act(async () => root.unmount())
  container.remove()
  requests.verify()
  vi.unstubAllGlobals()
})
async function render(role: 'Owner' | 'Viewer' = 'Owner', tenantId = 'alpha', token = 'token') {
  const tenant = { id: tenantId, name: tenantId, role, isDefault: true }
  await act(async () =>
    root.render(
      <AppShell
        registry={registry}
        initialViewId="redis"
        api={{ baseUrl: 'http://localhost', token }}
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
          activeTenantId: tenant.id,
          selectTenant: () => {},
          status: 'success',
          error: '',
        }}
      />,
    ),
  )
}
const visible = (element: Element) => !element.closest('[hidden]')
const text = () =>
  [...container.querySelectorAll('#app-content *')]
    .filter(visible)
    .map((element) => element.textContent)
    .join(' ')
async function click(name: string) {
  await act(async () => {
    const scope = container.querySelector('dialog[open]') ?? container
    const element = [...scope.querySelectorAll<HTMLElement>('button,a')].find(
      (element) =>
        visible(element) &&
        (element.textContent === name || element.getAttribute('aria-label') === name),
    )
    if (!element) throw new Error(`Missing button ${name}`)
    element.click()
  })
}
async function tab(name: string) {
  await act(async () =>
    [...container.querySelectorAll<HTMLButtonElement>('[role=tab]')]
      .find((element) => visible(element) && element.getAttribute('title') === name)!
      .click(),
  )
}
function field(name: string) {
  return [...container.querySelectorAll('label')]
    .find((label) => visible(label) && label.querySelector('.field__label')?.textContent === name)!
    .querySelector<HTMLInputElement | HTMLSelectElement>('input,select')!
}
async function fill(name: string, value: string) {
  await act(async () => {
    const input = field(name)
    Object.getOwnPropertyDescriptor(
      input instanceof HTMLSelectElement ? HTMLSelectElement.prototype : HTMLInputElement.prototype,
      'value',
    )!.set!.call(input, value)
    input.dispatchEvent(
      new Event(input instanceof HTMLSelectElement ? 'change' : 'input', { bubbles: true }),
    )
  })
}
async function open() {
  await render()
  await click('orders')
}

it('includes its registered catalog in the generic overview', async () => {
  await render()
  await click('Overview')
  expect(
    container.querySelector('section[aria-label="Resource catalogs"] a')?.getAttribute('href'),
  ).toContain('view=redis')
})

it('creates the backend Redis shape with one eligible target, profiles and normalized tags', async () => {
  await render()
  await click('New Redis')
  await fill('Name', 'new-cache')
  await fill('Tags', ' cache, cache, app ')
  await act(async () =>
    field('Name')
      .closest('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })),
  )
  const post = fetchMock.mock.calls.find(([, init]) => init?.method === 'POST')!
  expect(JSON.parse(post[1]!.body as string)).toEqual({
    name: 'new-cache',
    resourceGroupId: 'group',
    tags: ['cache', 'app'],
    spec: { storage: { sizeGb: 1, storageProfileId: 'disk' } },
    computeProfileId: 'small',
    placement: { isolationScopeId: 'scope', deploymentTargetId: 'target' },
  })
  expect(window.location.search).toContain('resource=database')
  for (const call of fetchMock.mock.calls.filter(([url]) =>
    /compute-profiles|deployment-targets|storage-profiles/.test(String(url)),
  )) {
    expect(new URL(String(call[0])).searchParams.get('resourceType')).toBe('pole/redis')
    if (/compute-profiles|storage-profiles/.test(String(call[0])))
      expect(new URL(String(call[0])).searchParams.get('deploymentTargetId')).toBe('target')
    expect(call[1]?.headers).toEqual(
      expect.objectContaining({ Authorization: 'Bearer token', 'X-Pole-Tenant-ID': 'alpha' }),
    )
  }
})

it('preserves drafts and typed errors, keeps compute snapshot on update and confirms dirty close', async () => {
  await open()
  await tab('Configuration')
  await fill('Tags', 'cache, updated')
  await tab('Overview')
  await click('Refresh Redis')
  await tab('Configuration')
  expect(field('Tags').value).toBe('cache, updated')
  expect(field('Storage (GiB)').disabled).toBe(true)
  await click('Close orders')
  expect(container.querySelector('dialog')?.textContent).toContain('Discard changes')
  await click('Cancel')
  failUpdate = true
  await act(async () =>
    field('Storage (GiB)')
      .closest('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })),
  )
  expect(text()).toContain('Invalid capacity.')
  expect(field('Tags').value).toBe('cache, updated')
  failUpdate = false
  await act(async () =>
    field('Storage (GiB)')
      .closest('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })),
  )
  const put = fetchMock.mock.calls.find(([, init]) => init?.method === 'PUT')!
  expect(JSON.parse(put[1]!.body as string)).toEqual({
    spec: { storage: { sizeGb: 1, storageProfileId: 'disk' } },
    tags: ['cache', 'updated'],
  })
})

it('pages and searches on the server while preserving rows after a failed refresh', async () => {
  await render()
  await click('Next page')
  expect(
    fetchMock.mock.calls.some(
      ([input]) => new URL(String(input)).searchParams.get('pageNumber') === '2',
    ),
  ).toBe(true)
  await fill('Search', 'cache')
  await act(async () =>
    field('Search')
      .closest('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })),
  )
  expect(
    fetchMock.mock.calls.some(
      ([input]) => new URL(String(input)).searchParams.get('search') === 'cache',
    ),
  ).toBe(true)
  failList = true
  await click('Refresh Redis services')
  expect(text()).toContain('orders')
  expect(text()).toContain('Catalog unavailable')
})

it('has read-only viewer controls and represents missing placement without fake readiness', async () => {
  redis.placement = null
  await render('Viewer')
  await click('orders')
  await tab('Configuration')
  expect(text()).toContain('Redis configuration is read-only.')
  expect(text()).not.toContain('Delete Redis')
  expect(text()).not.toContain('Save changes')
  await tab('Overview')
  expect(text()).toContain('Deployment metadata is unavailable.')
})

it('retries only failed deployment, including failed deletion, and confirms delete before sending it', async () => {
  redis.placement!.status = 'Failed'
  redis.placement!.failureCode = 'TransportFailure'
  redis.lifecycle = 'Deleting'
  await open()
  await click('Retry deployment')
  expect(
    fetchMock.mock.calls.some(
      ([url, init]) =>
        new URL(String(url)).pathname.endsWith('/resource-deployments/deployment/retry') &&
        init?.method === 'POST',
    ),
  ).toBe(true)
  expect(text()).not.toContain('Delete Redis')
  redis.lifecycle = 'Active'
  redis.placement!.status = 'Applied'
  await click('Refresh Redis')
  await click('Delete Redis')
  expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'DELETE')).toBe(false)
  await click('Cancel')
  await click('Delete Redis')
  await click('Delete Redis')
  expect(fetchMock.mock.calls.find(([, init]) => init?.method === 'DELETE')?.[1]?.headers).toEqual(
    expect.objectContaining({ 'X-Pole-Tenant-ID': 'alpha' }),
  )
})

it('aborts detail reads on tenant change and suppresses their late metadata', async () => {
  let resolve!: (response: Response) => void
  pendingDetails = new Promise((accept) => {
    resolve = accept
  })
  await open()
  const read = fetchMock.mock.calls.find(([url]) =>
    String(url).endsWith('/redis-services/database'),
  )!
  pendingDetails = undefined
  await render('Owner', 'beta')
  await act(async () => resolve(new Response(JSON.stringify({ ...initial, name: 'late-alpha' }))))
  expect((read[1]?.signal as AbortSignal).aborted).toBe(true)
  expect(text()).not.toContain('late-alpha')
})

it('preserves Redis drafts through credential renewal and uses the new token', async () => {
  await render()
  await click('orders')
  await tab('Configuration')
  await fill('Tags', 'local-draft')
  await render('Owner', 'alpha', 'renewed-token')
  expect(field('Tags').value).toBe('local-draft')
  await click('Refresh Redis')
  const reads = fetchMock.mock.calls.filter(([url]) =>
    String(url).endsWith('/redis-services/database'),
  )
  expect(reads.at(-1)![1]?.headers).toEqual(
    expect.objectContaining({ Authorization: 'Bearer renewed-token' }),
  )
  expect(field('Tags').value).toBe('local-draft')
})

it('reviews a known concurrent Redis revision before reapplying its draft', async () => {
  await open()
  await tab('Configuration')
  await fill('Tags', 'local-draft')
  redis = { ...redis, revision: 2, tags: ['server-tags'] }
  await click('Refresh Redis')
  expect(text()).toContain('The Redis configuration changed since revision 1')
  await act(async () =>
    field('Tags')
      .closest('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })),
  )
  expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'PUT')).toBe(false)
  await click('Review and reapply draft')
  await click('Reapply changes')
  expect(field('Tags').value).toBe('local-draft')
  await act(async () =>
    field('Tags')
      .closest('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })),
  )
  const put = fetchMock.mock.calls.find(([, init]) => init?.method === 'PUT')!
  expect(JSON.parse(put[1]!.body as string)).toEqual({ spec: redis.spec, tags: ['local-draft'] })
})

it('keeps Git-owned configuration read-only while allowing deployment retry', async () => {
  redis.gitOps = {
    sourceId: 'source',
    sourceUrl: 'https://example.test/repository',
    blocker: null,
    pendingRemoval: false,
    configurationApplied: true,
  }
  redis.placement!.status = 'Failed'
  await open()
  expect(text()).toContain('Git manages this configuration.')
  expect(text()).not.toContain('Delete Redis')
  await click('Retry deployment')
  await tab('Configuration')
  expect(text()).toContain('Redis configuration is read-only.')
  expect(text()).not.toContain('Save changes')
  expect(
    fetchMock.mock.calls.some(([, init]) => init?.method === 'PUT' || init?.method === 'DELETE'),
  ).toBe(false)
})
