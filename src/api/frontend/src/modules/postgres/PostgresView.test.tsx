// @vitest-environment jsdom
import { act, useLayoutEffect } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { strictFetch } from '../../test/http'
import { AppShell } from '../../shell/AppShell'
import { createAppRegistry } from '../../shell/moduleRegistry'
import { useQuerySession, type QuerySession } from '../../data/querySession'
import type { PostgresCluster } from '../../types'
import { postgresModule } from './PostgresModule'
import { PostgresView } from './PostgresView'

let querySession: QuerySession
function Page() {
  const session = useQuerySession()
  useLayoutEffect(() => {
    querySession = session
  }, [session])
  return <PostgresView />
}
const registry = createAppRegistry([
  { ...postgresModule, views: postgresModule.views.map((view) => ({ ...view, component: Page })) },
])
const initial: PostgresCluster = {
  id: 'database',
  name: 'orders',
  groupId: 'group',
  revision: 1,
  lifecycle: 'Active',
  spec: {
    version: 17,
    instances: 1,
    storage: 1,
    storageProfileId: 'disk',
    deploymentMode: 'Operator',
  },
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
    endpoints: [{ kind: 'PostgreSql', host: 'orders.internal', port: 5432, scheme: 'postgresql' }],
  },
}
let root: Root
let container: HTMLDivElement
let cluster: PostgresCluster
let mode: 'Operator' | 'StatefulSet'
let failUpdate: boolean
let conflictUpdate: boolean
let acceptedWithoutRefresh: boolean
let failDetails: boolean
let pendingReveal: Promise<Response> | undefined
let pendingDetails: Promise<Response> | undefined
const requests = strictFetch()
const fetchMock = requests.fetch
const json = (value: unknown) => Promise.resolve(new Response(JSON.stringify(value)))
const paged = (items: unknown[]) => ({
  items,
  pageNumber: 1,
  pageSize: 8,
  totalCount: items.length,
})
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
  window.history.replaceState({}, '', '/?view=postgres&resource=catalog')
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
  cluster = structuredClone(initial)
  mode = 'Operator'
  failUpdate = false
  conflictUpdate = false
  acceptedWithoutRefresh = false
  failDetails = false
  pendingReveal = undefined
  pendingDetails = undefined
  requests.reset()
  fetchMock.mockImplementation((input, init) => {
    const url = new URL(String(input), 'http://localhost')
    if (url.pathname.endsWith('/supported-versions'))
      return json({ from: 16, to: 17, defaultDeploymentMode: mode })
    if (url.pathname.endsWith('/connection-string'))
      return pendingReveal ?? json({ connectionString: 'ephemeral-credential' })
    if (url.pathname.endsWith('/password'))
      return json({ id: cluster.id, placement: cluster.placement })
    if (url.pathname.endsWith('/postgres/'))
      return init?.method === 'POST' ? json(cluster) : json(paged([cluster]))
    if (url.pathname.endsWith('/postgres/database')) {
      if (!init?.method && failDetails)
        return Promise.resolve(new Response('Metadata refresh unavailable', { status: 503 }))
      if (!init?.method && pendingDetails) return pendingDetails
      if (init?.method === 'PUT') {
        if (conflictUpdate) {
          cluster = { ...cluster, revision: 2, spec: { ...cluster.spec, instances: 3 } }
          return Promise.resolve(
            new Response(
              JSON.stringify({
                code: 'postgres.update.concurrent-change',
                title: 'Configuration changed',
              }),
              { status: 409 },
            ),
          )
        }
        if (failUpdate)
          return Promise.resolve(
            new Response(
              JSON.stringify({
                title: 'Validation failed',
                errors: { 'Body.Storage': ['Cannot shrink storage.'] },
              }),
              { status: 400 },
            ),
          )
        cluster = {
          ...cluster,
          revision: cluster.revision + 1,
          spec: { ...cluster.spec, ...JSON.parse(init.body as string) },
        }
        if (acceptedWithoutRefresh) failDetails = true
      }
      return json(cluster)
    }
    if (url.pathname.endsWith('/resource-groups'))
      return json(
        paged([{ id: 'group', name: 'Applications', tags: [], description: '', resourceCount: 1 }]),
      )
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
      return json([
        { id: 'small', displayName: 'Small', isDefault: true },
        { id: 'large', displayName: 'Large', isDefault: false },
      ])
    if (url.pathname.endsWith('/storage-profiles'))
      return json([{ id: 'disk', displayName: 'Standard disk', isDefault: true }])
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
        deploymentRevision: cluster.placement?.deploymentRevision,
        desiredGeneration: cluster.placement?.desiredGeneration,
        checkedAtUtc: new Date().toISOString(),
        observation: { kind: 'Unavailable', reason: 'Unsupported' },
      })
    if (url.pathname.endsWith('/resource-connections/endpoints'))
      return json({
        availability: 'Supported',
        endpoints: [],
        topology: { status: 'NotEvaluated', reasons: [] },
      })
    if (url.pathname.endsWith('/retry')) return json({})
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
        initialViewId="postgres"
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

it('registers typed resource routing, pages the catalog and discovers PostgreSQL choices', async () => {
  await render()
  await click('New PostgreSQL')
  expect(field('Isolation scope').value).toBe('scope')
  expect(field('Deployment target').value).toBe('target')
  for (const call of fetchMock.mock.calls.filter(([url]) =>
    /compute-profiles|deployment-targets|storage-profiles/.test(String(url)),
  )) {
    expect(new URL(String(call[0])).searchParams.get('resourceType')).toBe('pole/postgresql')
    if (/storage-profiles|compute-profiles/.test(String(call[0])))
      expect(new URL(String(call[0])).searchParams.get('deploymentTargetId')).toBe('target')
    expect(call[1]?.headers).toEqual(
      expect.objectContaining({ Authorization: 'Bearer token', 'X-Pole-Tenant-ID': 'alpha' }),
    )
  }
  await fill('Name', 'new-database')
  await fill('Database username', 'app')
  await fill('Database password', 'write-only-test-value')
  expect(localStorage.getItem('pole.postgres.workspace.v1.user.alpha')).not.toContain(
    'write-only-test-value',
  )
  await tab('All databases')
  await tab('New PostgreSQL')
  expect(field('Database username').value).toBe('')
  expect(field('Database password').value).toBe('')
  expect(field('Name').value).toBe('new-database')
})

it('uses the native server mode and forbids multiple instances and password rotation', async () => {
  mode = 'StatefulSet'
  cluster.spec.deploymentMode = 'StatefulSet'
  await render()
  await click('New PostgreSQL')
  expect(field('Instances').disabled).toBe(true)
  await tab('All databases')
  await click('orders')
  await tab('Configuration')
  expect(field('Storage per instance (GiB)').disabled).toBe(true)
  await tab('Credentials')
  expect(text()).toContain('Password rotation is unavailable for native StatefulSet databases.')
  expect([...container.querySelectorAll('input[type=password]')].filter(visible)).toHaveLength(0)
})

it('preserves configuration drafts through sections and refresh, confirms dirty close and maps field errors', async () => {
  await open()
  await tab('Configuration')
  await fill('Storage per instance (GiB)', '2')
  await tab('Overview')
  await click('Refresh database')
  await tab('Configuration')
  expect(field('Storage per instance (GiB)').value).toBe('2')
  await click('Close orders')
  expect(container.querySelector('dialog')?.textContent).toContain('Discard changes')
  await click('Cancel')
  failUpdate = true
  await act(async () =>
    field('Instances')
      .closest('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })),
  )
  expect(text()).toContain('Cannot shrink storage.')
  expect(field('Storage per instance (GiB)').value).toBe('2')
  failUpdate = false
  await act(async () =>
    field('Instances')
      .closest('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })),
  )
  const put = fetchMock.mock.calls.find(([, init]) => init?.method === 'PUT')!
  expect(JSON.parse(put[1]!.body as string)).toEqual({ expectedRevision: 1, storage: 2 })
})

it('keeps Git-owned configuration read-only while allowing credential operations', async () => {
  cluster.gitOps = {
    sourceId: 'source',
    sourceUrl: '/api/v1/gitops/sources/source',
    blocker: null,
    pendingRemoval: false,
    configurationApplied: true,
  }
  await open()
  expect(text()).toContain('Git manages this configuration.')
  expect(text()).not.toContain('Delete database')
  await tab('Configuration')
  expect(text()).toContain('Database configuration is read-only.')
  await tab('Credentials')
  await click('Reveal connection string')
  expect(container.querySelector('textarea')?.value).toBe('ephemeral-credential')
  expect(text()).toContain('Rotate password')
})

it('clears revealed values on section change, role downgrade and tenant change', async () => {
  await open()
  await tab('Credentials')
  await click('Reveal connection string')
  expect(container.querySelector('textarea')?.value).toBe('ephemeral-credential')
  expect(JSON.stringify(localStorage)).not.toContain('ephemeral-credential')
  await tab('Overview')
  expect(container.querySelector('textarea')).toBeNull()
  await tab('Credentials')
  expect(container.querySelector('textarea')).toBeNull()
  await click('Reveal connection string')
  await render('Viewer')
  expect(container.querySelector('textarea')).toBeNull()
  expect(text()).not.toContain('Delete database')
  expect(
    fetchMock.mock.calls.filter(([url]) => String(url).endsWith('/connection-string')),
  ).toHaveLength(2)
  await render('Owner', 'beta')
  expect(container.querySelector('textarea')).toBeNull()
})

it('does not reveal a late response after browser blur', async () => {
  let resolve!: (value: Response) => void
  pendingReveal = new Promise((accept) => {
    resolve = accept
  })
  await open()
  await tab('Credentials')
  await click('Reveal connection string')
  const call = fetchMock.mock.calls.find(([url]) => String(url).endsWith('/connection-string'))!
  await act(async () => {
    window.dispatchEvent(new Event('blur'))
    resolve(new Response(JSON.stringify({ connectionString: 'late-secret' })))
  })
  expect((call[1]?.signal as AbortSignal).aborted).toBe(true)
  expect(container.querySelector('textarea')).toBeNull()
})

it('keeps credentials unavailable until the current generation is applied', async () => {
  cluster.placement.observedGeneration = 0
  await open()
  await tab('Credentials')
  const reveal = [...container.querySelectorAll<HTMLButtonElement>('button')].find(
    (element) => element.textContent === 'Reveal connection string',
  )!
  expect(reveal.disabled).toBe(true)
})

it('polls the actual wire pending state without claiming readiness', async () => {
  cluster.placement.status = 'Pending'
  cluster.placement.observedGeneration = 0
  const interval = vi.spyOn(window, 'setInterval')
  await open()
  expect(interval).toHaveBeenCalledWith(expect.any(Function), 2500)
  expect(text()).toContain('Applied reports workload reconciliation.')
  interval.mockRestore()
})

it('confirms deletion before issuing a tenant-scoped mutation', async () => {
  await open()
  await click('Delete database')
  expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'DELETE')).toBe(false)
  await click('Cancel')
  await click('Delete database')
  await click('Delete database')
  const deletion = fetchMock.mock.calls.find(([, init]) => init?.method === 'DELETE')!
  expect(deletion[1]?.headers).toEqual(
    expect.objectContaining({ Authorization: 'Bearer token', 'X-Pole-Tenant-ID': 'alpha' }),
  )
})

it('allows a writer to retry failed deletion without enabling configuration changes', async () => {
  cluster.lifecycle = 'Deleting'
  cluster.placement.status = 'Failed'
  cluster.placement.desiredOperation = 'Delete'
  await open()
  await click('Retry deployment')
  expect(
    fetchMock.mock.calls.some(
      ([url, init]) =>
        new URL(String(url)).pathname.endsWith('/resource-deployments/deployment/retry') &&
        init?.method === 'POST',
    ),
  ).toBe(true)
  expect(text()).not.toContain('Delete database')
  await tab('Configuration')
  expect(text()).toContain('Database configuration is read-only.')
})

it('confirms closing an unsaved password rotation and clears it after switching resources', async () => {
  await open()
  await tab('Credentials')
  await fill('New database password', 'unsubmitted-value')
  await click('Close orders')
  expect(container.querySelector('dialog')?.textContent).toContain('Discard changes to orders')
  await click('Cancel')
  await tab('All databases')
  await tab('orders')
  expect(field('New database password').value).toBe('')
  expect(JSON.stringify(localStorage)).not.toContain('unsubmitted-value')
})

it('requires explicit reapplication of changed fields after a concurrent scale', async () => {
  await open()
  await tab('Configuration')
  await fill('Compute profile', 'large')
  cluster = { ...cluster, revision: 2, spec: { ...cluster.spec, instances: 3, storage: 10 } }
  await click('Refresh database')
  expect(field('Instances').value).toBe('1')
  expect(text()).toContain('The server configuration changed since revision 1')
  await act(async () =>
    field('Instances')
      .closest('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })),
  )
  expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'PUT')).toBe(false)
  await click('Review and reapply draft')
  await click('Reapply changes')
  expect(field('Instances').value).toBe('3')
  expect(field('Storage per instance (GiB)').value).toBe('10')
  expect(field('Compute profile').value).toBe('large')
  await act(async () =>
    field('Instances')
      .closest('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })),
  )
  const put = fetchMock.mock.calls.find(([, init]) => init?.method === 'PUT')!
  expect(JSON.parse(put[1]!.body as string)).toEqual({
    expectedRevision: 2,
    computeProfileId: 'large',
  })
})

it('discards a conflicting draft in favor of the current server revision', async () => {
  await open()
  await tab('Configuration')
  await fill('Storage per instance (GiB)', '2')
  cluster = { ...cluster, revision: 2, spec: { ...cluster.spec, instances: 3, storage: 10 } }
  await click('Refresh database')
  await click('Discard changes')
  await click('Discard changes')
  expect(field('Instances').value).toBe('3')
  expect(field('Storage per instance (GiB)').value).toBe('10')
  expect(text()).not.toContain('The server configuration changed since revision')
})

it('refreshes a stale write conflict and keeps its draft until explicit review', async () => {
  await open()
  await tab('Configuration')
  await fill('Storage per instance (GiB)', '2')
  conflictUpdate = true
  await act(async () =>
    field('Instances')
      .closest('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })),
  )
  expect(text()).toContain('The server configuration changed since revision 1')
  expect(field('Storage per instance (GiB)').value).toBe('2')
  conflictUpdate = false
  await click('Review and reapply draft')
  await click('Reapply changes')
  expect(field('Instances').value).toBe('3')
  await act(async () =>
    field('Instances')
      .closest('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })),
  )
  const puts = fetchMock.mock.calls.filter(([, init]) => init?.method === 'PUT')
  expect(JSON.parse(puts[1]![1]!.body as string)).toEqual({ expectedRevision: 2, storage: 2 })
})

it('preserves its draft on token renewal and sends the current credential', async () => {
  await open()
  await tab('Configuration')
  await fill('Storage per instance (GiB)', '2')
  const before = fetchMock.mock.calls.length
  await render('Owner', 'alpha', 'renewed-token')
  expect(field('Storage per instance (GiB)').value).toBe('2')
  await click('Refresh database')
  const reads = fetchMock.mock.calls
    .slice(before)
    .filter(([url]) => String(url).endsWith('/postgres/database'))
  expect(reads).toHaveLength(1)
  expect(reads[0]![1]?.headers).toEqual(
    expect.objectContaining({ Authorization: 'Bearer renewed-token' }),
  )
  expect(field('Storage per instance (GiB)').value).toBe('2')
})

it('does not cancel a slow PostgreSQL refresh when polling repeats', async () => {
  cluster.placement.status = 'Pending'
  cluster.placement.observedGeneration = 0
  const interval = vi.spyOn(window, 'setInterval')
  await open()
  let resolve!: (response: Response) => void
  pendingDetails = new Promise((accept) => {
    resolve = accept
  })
  await click('Refresh database')
  const reads = fetchMock.mock.calls.filter(([url]) => String(url).endsWith('/postgres/database'))
  const call = reads.at(-1)!
  const callback = interval.mock.calls.find(
    ([, milliseconds]) => milliseconds === 2500,
  )![0] as () => void
  await act(async () => {
    callback()
    callback()
  })
  expect((call[1]?.signal as AbortSignal).aborted).toBe(false)
  expect(
    fetchMock.mock.calls.filter(([url]) => String(url).endsWith('/postgres/database')),
  ).toHaveLength(reads.length)
  await act(async () => {
    pendingDetails = undefined
    cluster.placement.status = 'Applied'
    cluster.placement.observedGeneration = 1
    resolve(new Response(JSON.stringify(cluster)))
  })
  expect(text()).toContain('Database overview')
  expect(text()).toContain('Applied')
  interval.mockRestore()
})

it('keeps passwords and revealed connections outside both query and mutation caches', async () => {
  await render()
  await click('New PostgreSQL')
  await fill('Name', 'new-database')
  await fill('Database username', 'app')
  await fill('Database password', 'write-only-test-value')
  await act(async () =>
    field('Name')
      .closest('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })),
  )
  const cache = () =>
    JSON.stringify({
      queries: querySession.client
        .getQueryCache()
        .getAll()
        .map((query) => query.state),
      mutations: querySession.client
        .getMutationCache()
        .getAll()
        .map((mutation) => mutation.state),
    })
  expect(cache()).not.toContain('write-only-test-value')
  await tab('Credentials')
  await click('Reveal connection string')
  expect(container.querySelector('textarea')?.value).toBe('ephemeral-credential')
  expect(cache()).not.toContain('ephemeral-credential')
  expect(querySession.client.getMutationCache().getAll()).toHaveLength(0)
})

it('keeps an accepted write distinct from a failed refresh and blocks duplicate saves', async () => {
  await open()
  await tab('Configuration')
  await fill('Storage per instance (GiB)', '2')
  acceptedWithoutRefresh = true
  await act(async () =>
    field('Instances')
      .closest('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })),
  )
  expect(text()).toContain(
    'Configuration was accepted. Refresh the database to load its new revision.',
  )
  expect(text()).toContain('Metadata refresh unavailable')
  expect(
    [...container.querySelectorAll<HTMLButtonElement>('button')].find(
      (button) => visible(button) && button.textContent === 'Save changes',
    )!.disabled,
  ).toBe(true)
  await act(async () =>
    field('Instances')
      .closest('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })),
  )
  expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'PUT')).toHaveLength(1)
  failDetails = false
  await click('Refresh database')
  expect(field('Storage per instance (GiB)').value).toBe('2')
  expect(text()).not.toContain('Refresh the database to load its new revision.')
})
