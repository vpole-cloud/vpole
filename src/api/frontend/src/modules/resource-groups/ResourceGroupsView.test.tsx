// @vitest-environment jsdom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { strictFetch } from '../../test/http'
import { AppShell } from '../../shell/AppShell'
import { useAppServices } from '../../shell/AppContext'
import { createAppRegistry } from '../../shell/moduleRegistry'
import { resourceGroupsModule } from '../platform/PlatformModules'
import { ResourceGroupsView } from './ResourceGroupsView'
import type { ResourceGroup } from '../../types'

function OtherProvider() {
  const { navigation } = useAppServices()
  return <p>Independent provider: {new URLSearchParams(navigation.search).get('resource')}</p>
}
const registry = createAppRegistry([
  {
    ...resourceGroupsModule,
    views: resourceGroupsModule.views.map((view) => ({ ...view, component: ResourceGroupsView })),
  },
  {
    id: 'other',
    views: [{ id: 'widgets', label: 'Widgets', component: OtherProvider, access: 'tenant' }],
    resourceTypes: [{ id: 'test/widget', label: 'Widget', viewId: 'widgets' }],
  },
])
const group: ResourceGroup = {
  id: 'group',
  name: 'Applications',
  description: 'Production workloads',
  tags: ['production'],
  resourceCount: 2,
}
const second = { ...group, id: 'second', name: 'Workers' }
const outside = { ...group, id: 'outside', name: 'Outside first page' }
const refs = [
  {
    registrationId: 'registered',
    resourceId: 'widget',
    resourceTypeId: 'test/widget',
    displayName: 'My widget',
  },
  {
    registrationId: 'unknown',
    resourceId: 'removed',
    resourceTypeId: 'removed/provider',
    displayName: 'Unavailable workload',
  },
]
let container: HTMLDivElement
let root: Root
let records: ResourceGroup[]
let failList: boolean
let failSave: boolean
const requests = strictFetch()
const fetchMock = requests.fetch
const json = (body: unknown, status = 200) =>
  Promise.resolve(
    new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }),
  )
const paged = (items: unknown[], params: URLSearchParams) => {
  const pageNumber = Number(params.get('pageNumber') ?? 1),
    pageSize = Number(params.get('pageSize') ?? 8)
  return {
    items: items.slice((pageNumber - 1) * pageSize, pageNumber * pageSize),
    totalCount: items.length,
    pageNumber,
    pageSize,
  }
}
const storageKey = 'pole.resource-groups.workspace.v1.user.alpha'
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
  window.history.replaceState({}, '', '/')
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
  records = [group, second, outside]
  failList = false
  failSave = false
  requests.reset()
  fetchMock.mockImplementation((input, init) => {
    const url = new URL(String(input), 'http://localhost')
    if (url.pathname.endsWith('/resource-groups')) {
      if (init?.method === 'POST') {
        if (failSave)
          return json(
            { title: 'Validation failed', errors: { 'Body.Name': ['Choose a unique name.'] } },
            400,
          )
        const created = {
          ...group,
          ...JSON.parse(String(init.body)),
          id: 'created',
          resourceCount: 0,
        }
        records.push(created)
        return json(created)
      }
      if (failList) return json({ title: 'Inventory is unavailable' }, 503)
      return json(
        paged(
          records.filter((item) =>
            item.name.toLowerCase().includes((url.searchParams.get('search') ?? '').toLowerCase()),
          ),
          url.searchParams,
        ),
      )
    }
    if (url.pathname.endsWith('/resources')) return json(paged(refs, url.searchParams))
    const id = url.pathname.match(/\/resource-groups\/([^/]+)$/)?.[1]
    const found = records.find((item) => item.id === id)
    if (id) {
      if (!found) return json({ title: 'Resource group not found' }, 404)
      if (init?.method === 'PUT') {
        if (failSave)
          return json(
            {
              title: 'Validation failed',
              errors: { 'Body.Description': ['Description is rejected.'] },
            },
            400,
          )
        records = records.map((item) =>
          item.id === id ? { ...item, ...JSON.parse(String(init.body)) } : item,
        )
        return json(records.find((item) => item.id === id))
      }
      if (init?.method === 'DELETE') {
        records = records.filter((item) => item.id !== id)
        return json({})
      }
      return json(found)
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
    return requests.unhandled(input, init)
  })
  vi.stubGlobal('fetch', fetchMock)
})
afterEach(async () => {
  await act(async () => root.unmount())
  container.remove()
  requests.verify()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})
function render(
  active = 'alpha',
  userId = 'user',
  authenticated = true,
  role = 'Owner' as 'Owner' | 'Viewer',
  token = 'token',
) {
  const tenant = { id: active, name: active, role, isDefault: true }
  root.render(
    <AppShell
      registry={registry}
      initialViewId="resource-groups"
      api={{ baseUrl: 'http://localhost', token }}
      auth={{
        initialized: true,
        authenticated,
        userId,
        isPlatformAdmin: false,
        login: () => {},
        logout: () => {},
      }}
      tenants={{
        items: ['alpha', 'beta'].map((id) => ({ ...tenant, id, name: id })),
        activeTenant: tenant,
        activeTenantId: active,
        status: 'success',
        error: '',
        selectTenant: vi.fn(),
      }}
    />,
  )
}
const main = () => container.querySelector('#app-content')!
const visible = (element: Element) => !element.closest('[hidden]')
const heading = () => [...main().querySelectorAll('h1')].find(visible)?.textContent
async function click(label: string, within: Element = main()) {
  await act(async () => {
    const node = [
      ...within.querySelectorAll<HTMLButtonElement | HTMLAnchorElement>('button, a'),
    ].find(
      (item) =>
        visible(item) && (item.textContent === label || item.getAttribute('aria-label') === label),
    )
    expect(node, `Missing action: ${label}`).toBeTruthy()
    node!.click()
  })
}
const field = (placeholder: string) =>
  [...main().querySelectorAll<HTMLInputElement | HTMLTextAreaElement>('input, textarea')].find(
    (item) => visible(item) && item.placeholder === placeholder,
  )!
async function fill(input: HTMLInputElement | HTMLTextAreaElement, value: string) {
  await act(async () => {
    Object.getOwnPropertyDescriptor(
      input instanceof HTMLTextAreaElement
        ? HTMLTextAreaElement.prototype
        : HTMLInputElement.prototype,
      'value',
    )!.set!.call(input, value)
    input.dispatchEvent(new Event('input', { bubbles: true }))
  })
}
async function submit() {
  await act(async () => {
    ;[...main().querySelectorAll('form')]
      .find(visible)!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
  })
}
async function route(search: string) {
  await act(async () => {
    window.history.pushState({}, '', `/${search}`)
    window.dispatchEvent(new PopStateEvent('popstate'))
  })
}

it('opens a direct group URL outside the first catalog page and validates missing references', async () => {
  window.history.replaceState(
    {},
    '',
    '/?view=resource-groups&tenant=alpha&resource=outside&pageSize=1',
  )
  await act(async () => render())
  expect(heading()).toBe(outside.name)
  const read = fetchMock.mock.calls.find(([url]) =>
    String(url).endsWith('/resource-groups/outside'),
  )!
  expect(read[1]?.headers).toMatchObject({
    Authorization: 'Bearer token',
    'X-Pole-Tenant-ID': 'alpha',
  })
  await route('?view=resource-groups&tenant=alpha&resource=missing')
  expect(main().textContent).toContain('Unable to open this group.')
  expect(heading()).toBeUndefined()
  expect(
    main().querySelectorAll('#resource-groups-workspace > [role=tablist] [role=tab]'),
  ).toHaveLength(3)
})

it('pages and searches groups on the server and retains loaded rows after a failure', async () => {
  window.history.replaceState(
    {},
    '',
    '/?view=resource-groups&resource=catalog&pageSize=1&tenant=alpha',
  )
  await act(async () => render())
  await click('Next page')
  expect(main().textContent).toContain('Workers')
  expect(main().textContent).not.toContain('Applications')
  failList = true
  await click('Refresh resource groups')
  expect(main().textContent).toContain('Workers')
  expect(main().textContent).toContain('Inventory is unavailable')
  failList = false
  await fill(field('Find a group by name'), 'Outside')
  await submit()
  expect(new URLSearchParams(window.location.search).get('page')).toBe('1')
  expect(new URLSearchParams(window.location.search).get('search')).toBe('Outside')
  expect(main().textContent).toContain(outside.name)
})

it('keeps metadata drafts in memory, guards close and cross-provider navigation, and renders field validation', async () => {
  window.history.replaceState(
    {},
    '',
    '/?view=resource-groups&resource=group&section=configuration&tenant=alpha',
  )
  await act(async () => render())
  await fill(field('Boundary for production workloads and shared services.'), 'Private draft')
  await click('All groups')
  await click('Applications')
  await click('Configuration')
  expect(field('Boundary for production workloads and shared services.').value).toBe(
    'Private draft',
  )
  expect(localStorage.getItem(storageKey)).not.toContain('Private draft')
  await click('Close Applications')
  expect(container.querySelector('dialog')?.textContent).toContain('Discard changes')
  await click('Cancel', container.querySelector('dialog')!)
  await click('Widgets', container)
  expect(container.querySelector('dialog')?.textContent).toContain('Discard unsaved changes')
  await click('Cancel', container.querySelector('dialog')!)
  failSave = true
  await submit()
  expect(field('Boundary for production workloads and shared services.').value).toBe(
    'Private draft',
  )
  expect(
    field('Boundary for production workloads and shared services.').getAttribute('aria-invalid'),
  ).toBe('true')
  expect(main().textContent).toContain('Description is rejected.')
  failSave = false
  await submit()
  expect(records[0].description).toBe('Private draft')
  expect(main().textContent).toContain('Metadata saved for Applications.')
})

it('creates a group in its own guarded draft tab and keeps validation failures editable', async () => {
  await act(async () => render())
  await click('New group')
  await fill(field('prod-apps'), 'New environment')
  await click('Close New group')
  expect(container.querySelector('dialog')).toBeTruthy()
  await click('Cancel', container.querySelector('dialog')!)
  failSave = true
  await submit()
  expect(field('prod-apps').value).toBe('New environment')
  expect(main().textContent).toContain('Choose a unique name.')
  expect(localStorage.getItem(storageKey)).not.toContain('New environment')
  failSave = false
  await submit()
  expect(heading()).toBe('New environment')
  expect(new URLSearchParams(window.location.search).get('resource')).toBe('created')
})

it('restores only safe group identities and preferences for the current user and tenant', async () => {
  localStorage.setItem(
    storageKey,
    JSON.stringify({
      activeId: 'outside',
      tabs: [
        { id: 'outside', section: 'configuration', description: 'Do not restore' },
        { id: 'outside', section: 'resources' },
        { id: 'create' },
        { id: 123 },
      ],
    }),
  )
  await act(async () => render())
  expect(new URLSearchParams(window.location.search).get('resource')).toBe('outside')
  expect(field('Boundary for production workloads and shared services.').value).toBe(
    'Production workloads',
  )
  expect(JSON.parse(localStorage.getItem(storageKey)!).tabs).toEqual([
    { id: 'outside', section: 'configuration', resourcePage: 1, resourcePageSize: 20 },
  ])
  await act(async () => render('alpha', 'another-user'))
  expect(
    main().querySelectorAll('#resource-groups-workspace > [role=tablist] [role=tab]'),
  ).toHaveLength(2) // URL remains authoritative; stored tabs do not carry over.
  await route('?view=resource-groups&tenant=beta&resource=catalog')
  expect(
    main().querySelectorAll('#resource-groups-workspace > [role=tablist] [role=tab]'),
  ).toHaveLength(1)
})

it('restores group-resource pagination from history URLs and routes to the resource provider', async () => {
  window.history.replaceState(
    {},
    '',
    '/?view=resource-groups&resource=group&tenant=alpha&resourcePageSize=1',
  )
  await act(async () => render())
  const firstUrl = window.location.search
  await click('Next page')
  const secondUrl = window.location.search
  expect(main().textContent).toContain('Unavailable workload')
  expect(main().textContent).not.toContain('My widget')
  await route(firstUrl)
  expect(main().textContent).toContain('My widget')
  await route(secondUrl)
  expect(main().textContent).toContain('Unavailable workload')
  await route(firstUrl)
  await click('My widget')
  expect(main().textContent).toContain('Independent provider: widget')
  expect(new URLSearchParams(window.location.search).get('resourceType')).toBe('test/widget')
})

it('opens group and resource tree links through registrations and handles unavailable providers safely', async () => {
  localStorage.setItem(
    'pole.resource-tree.v1.user.alpha',
    JSON.stringify({ open: true, page: 1, expanded: ['group', 'stale'] }),
  )
  await act(async () => render())
  const tree = container.querySelector('[aria-label="Resource tree"]')!
  const link = [...tree.querySelectorAll('a')].find((item) => item.textContent === 'My widget')!
  expect(link.getAttribute('href')).toContain('resourceType=test%2Fwidget')
  expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/stale/'))).toBe(false)
  await click('My widget', tree)
  expect(main().textContent).toContain('Independent provider: widget')
  await click('Unavailable workload', tree)
  expect(main().textContent).toContain('This resource provider is unavailable.')
  expect(main().textContent).toContain('removed/provider')
  expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/removed'))).toBe(false)
  await click('Applications', tree)
  expect(heading()).toBe('Applications')
  await click('Applications', tree)
  expect(
    main().querySelectorAll('#resource-groups-workspace > [role=tablist] [role=tab]'),
  ).toHaveLength(2)
})

it('restores tree pagination and cancels child reads when a group is collapsed', async () => {
  localStorage.setItem(
    'pole.resource-tree.v1.user.alpha',
    JSON.stringify({ open: true, page: 2, pageSize: 1, expanded: ['second'] }),
  )
  const base = fetchMock.getMockImplementation()!
  let complete!: (response: Response) => void
  const pending = new Promise<Response>((resolve) => {
    complete = resolve
  })
  fetchMock.mockImplementation((url, init) =>
    String(url).includes('/second/resources') ? pending : base(url, init),
  )
  await act(async () => render())
  const tree = container.querySelector('[aria-label="Resource tree"]')!
  expect(tree.textContent).toContain('Page 2 of 3')
  expect(tree.textContent).not.toContain('Applications')
  const request = fetchMock.mock.calls.find(([url]) => String(url).includes('/second/resources'))!
  await click('Collapse Workers', tree)
  expect((request[1]?.signal as AbortSignal).aborted).toBe(true)
  await act(async () =>
    complete(
      new Response(
        JSON.stringify({
          items: [{ ...refs[0], displayName: 'Stale child' }],
          totalCount: 1,
          pageNumber: 1,
          pageSize: 8,
        }),
      ),
    ),
  )
  expect(tree.textContent).not.toContain('Stale child')
  await click('Next page', tree)
  expect(tree.textContent).toContain(outside.name)
  expect(JSON.parse(localStorage.getItem('pole.resource-tree.v1.user.alpha')!).page).toBe(3)
})

it('falls back to a usable catalog when browser storage is disabled', async () => {
  vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
    throw new Error('Disabled storage')
  })
  vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
    throw new Error('Disabled storage')
  })
  await act(async () => render())
  expect(heading()).toBe('Environment boundaries')
  await click('Applications')
  expect(heading()).toBe('Applications')
})

it('aborts and ignores old tenant detail and tree results, then clears reads on logout', async () => {
  const base = fetchMock.getMockImplementation()!
  let complete!: (response: Response) => void
  const pending = new Promise<Response>((resolve) => {
    complete = resolve
  })
  fetchMock.mockImplementation((url, init) =>
    String(url).endsWith('/resource-groups/group') &&
    new Headers(init?.headers).get('X-Pole-Tenant-ID') === 'alpha'
      ? pending
      : base(url, init),
  )
  window.history.replaceState({}, '', '/?view=resource-groups&resource=group&tenant=alpha')
  await act(async () => render())
  const old = fetchMock.mock.calls.find(([url]) => String(url).endsWith('/resource-groups/group'))!
  await route('?view=resource-groups&resource=catalog&tenant=beta')
  expect((old[1]?.signal as AbortSignal).aborted).toBe(true)
  await act(async () =>
    complete(new Response(JSON.stringify({ ...group, name: 'Stale private group' }))),
  )
  expect(container.textContent).not.toContain('Stale private group')
  const count = fetchMock.mock.calls.length
  await act(async () => render('beta', 'user', false))
  expect(container.querySelector('[aria-label="Resource tree"]')).toBeNull()
  expect(fetchMock.mock.calls).toHaveLength(count)
})

it('blocks unknown tenant links before group or tree requests and keeps viewer access read-only', async () => {
  window.history.replaceState({}, '', '/?view=resource-groups&resource=group&tenant=foreign')
  await act(async () => render())
  expect(fetchMock).not.toHaveBeenCalled()
  expect(main().textContent).toContain('This tenant is unavailable')
  await route('?view=resource-groups&resource=group&section=configuration&tenant=alpha')
  await act(async () => render('alpha', 'user', true, 'Viewer'))
  expect(main().textContent).toContain('Tenant viewer access is read-only.')
  expect(main().querySelector('textarea')).toBeNull()
})

it('preserves a group metadata draft across token renewal', async () => {
  window.history.replaceState(
    {},
    '',
    '/?view=resource-groups&resource=group&section=configuration&tenant=alpha',
  )
  await act(async () => render())
  await fill(field('Boundary for production workloads and shared services.'), 'Private draft')
  await act(async () => render('alpha', 'user', true, 'Owner', 'renewed-token'))
  expect(field('Boundary for production workloads and shared services.').value).toBe(
    'Private draft',
  )
  await click('Refresh group details')
  const reads = fetchMock.mock.calls.filter(([url]) =>
    String(url).endsWith('/resource-groups/group'),
  )
  expect(reads.at(-1)![1]?.headers).toEqual(
    expect.objectContaining({ Authorization: 'Bearer renewed-token' }),
  )
  expect(field('Boundary for production workloads and shared services.').value).toBe(
    'Private draft',
  )
})
