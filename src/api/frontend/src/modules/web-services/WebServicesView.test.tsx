// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

// @vitest-environment jsdom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { createTestApp } from '../../test/TestApp'
import { jsonResponse as json, strictFetch } from '../../test/http'
import { resourceGroup, webService } from '../../test/fixtures'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { AppShell } from '../../shell/AppShell'
import { createAppRegistry } from '../../shell/moduleRegistry'
import { webServicesModule } from './WebServicesModule'
import { WebServicesView } from './WebServicesView'
import type { IsolationScope, WebService } from '../../types'

const registry = createAppRegistry([
  {
    ...webServicesModule,
    views: webServicesModule.views.map((view) => ({ ...view, component: WebServicesView })),
  },
  {
    id: 'test-groups',
    views: [
      {
        id: 'resource-groups',
        label: 'Resource groups',
        component: () => <p>Group provider</p>,
        access: 'tenant',
      },
    ],
  },
])
const group = resourceGroup()
const scope = (id: string, resourceGroupId = group.id, isDefault = false): IsolationScope => ({
  id,
  resourceGroupId,
  name: isDefault ? 'default' : 'Production',
  isDefault,
  lifecycle: 'active',
  revision: 1,
  createdAtUtc: '',
  updatedAtUtc: '',
})
const target = {
  id: 'target',
  displayName: 'Local target',
  workloadConfigurationRevision: 1,
  hasPublicIngress: true,
}
const service = webService()
const secondService: WebService = { ...service, id: 'second', name: 'worker' }
let root: Root
let container: HTMLDivElement
let created: boolean
let failCreate: boolean
let failUpdate: boolean
let pendingCreate: Promise<Response> | null
let records: Map<string, WebService>
const selectTenant = vi.fn()
const requests = strictFetch()
const fetchMock = requests.fetch
const page = (items: unknown[]) => ({ items, totalCount: items.length, pageNumber: 1, pageSize: 8 })
const storageKey = 'pole.web-services.workspace.v1.user.alpha'

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
  ;({ container, root } = createTestApp())
  created = false
  failCreate = false
  failUpdate = false
  pendingCreate = null
  records = new Map([
    [service.id, service],
    [secondService.id, secondService],
  ])
  selectTenant.mockReset()
  requests.reset()
  fetchMock.mockImplementation((url, init) => {
    const path = new URL(String(url), 'http://localhost').pathname
    if (path.endsWith('/web-services') && init?.method === 'POST') {
      if (pendingCreate) return pendingCreate
      if (failCreate)
        return Promise.resolve(new Response('This name already exists.', { status: 409 }))
      created = true
      return json({ webServiceId: service.id, deployments: service.deployments })
    }
    if (path.endsWith('/web-services')) return json(page(created ? [...records.values()] : []))
    const match = path.match(/\/web-services\/([^/]+)$/)
    if (match) {
      const resource = records.get(match[1])
      if (!resource)
        return Promise.resolve(
          new Response('This service was removed or is unavailable.', { status: 404 }),
        )
      if (init?.method === 'PUT') {
        if (failUpdate)
          return Promise.resolve(
            new Response('Configuration changed. Refresh and retry.', { status: 409 }),
          )
        const request = JSON.parse(init.body as string)
        if (
          request.expectedRevision !== undefined &&
          request.expectedRevision !== resource.activeRevision
        ) {
          return Promise.resolve(
            new Response('Configuration changed. Refresh and retry.', { status: 409 }),
          )
        }
        const updated = {
          ...resource,
          activeRevision: resource.activeRevision + 1,
          spec: request.spec,
        }
        records.set(resource.id, updated)
        return json({
          id: updated.id,
          resourceRevision: updated.activeRevision,
          deployments: updated.deployments,
        })
      }
      return json(resource)
    }
    if (path.endsWith('/resource-groups')) return json(page([group]))
    if (path.endsWith('/deployment-targets/available')) return json([target])
    if (path.endsWith('/compute-profiles/available'))
      return json([{ id: 'profile', displayName: 'Default', isDefault: true }])
    if (path.endsWith('/releases')) return json({ activeRevision: 1, releases: page([]) })
    if (path.endsWith('/source-build-jobs')) return json(page([]))
    if (
      path.endsWith('/isolation-scopes') ||
      path.endsWith('/source-build-configurations') ||
      path.endsWith('/git-provider-connections') ||
      path.endsWith('/source-repositories')
    )
      return json([])
    if (path === '/api/v1/resource-deployments/deployment/observation')
      return json({
        deploymentRevision: 1,
        desiredGeneration: 1,
        checkedAtUtc: new Date().toISOString(),
        observation: { kind: 'Unavailable', reason: 'Unsupported' },
      })
    if (path === '/api/v1/resource-connections/endpoints')
      return json({
        endpoints: [],
        availability: 'Unsupported',
        topology: { status: 'NotEvaluated' },
      })
    return requests.unhandled(url, init)
  })
  vi.stubGlobal('fetch', fetchMock)
})
afterEach(async () => {
  await act(async () => {
    root.unmount()
  })
  container.remove()
  vi.unstubAllGlobals()
  requests.verify()
})

function render(
  tenantId = 'alpha',
  role: 'Owner' | 'Viewer' = 'Owner',
  authenticated = true,
  token = 'test-token',
) {
  const tenant = { id: tenantId, name: tenantId, role, isDefault: true }
  root.render(
    <AppShell
      registry={registry}
      initialViewId="web-services"
      api={{ baseUrl: 'http://localhost', token }}
      auth={{
        initialized: true,
        authenticated,
        userId: 'user',
        isPlatformAdmin: false,
        login: () => {},
        logout: () => {},
      }}
      tenants={{
        items: [tenant, ...(tenantId === 'beta' ? [] : [{ ...tenant, id: 'beta', name: 'beta' }])],
        activeTenant: tenant,
        activeTenantId: tenantId,
        status: 'success',
        error: '',
        selectTenant,
      }}
    />,
  )
}
const visible = (element: Element) => !element.closest('[hidden]')
const button = (label: string) =>
  [
    ...(container.querySelector('dialog') ?? container).querySelectorAll<HTMLButtonElement>(
      'button',
    ),
  ].find(
    (node) =>
      visible(node) && (node.textContent === label || node.getAttribute('aria-label') === label),
  )!
const click = async (label: string) => {
  await act(async () => {
    const action = [
      ...(container.querySelector('dialog') ?? container).querySelectorAll<
        HTMLButtonElement | HTMLAnchorElement
      >('button, a'),
    ].find(
      (node) =>
        visible(node) && (node.textContent === label || node.getAttribute('aria-label') === label),
    )!
    action.click()
  })
}
const workspaceTab = (name: string) =>
  [
    ...container.querySelectorAll<HTMLButtonElement>(
      '#web-services-workspace > [role="tablist"] [role="tab"]',
    ),
  ].find((node) => node.title === name)!
async function selectTab(name: string) {
  await act(async () => {
    workspaceTab(name).click()
  })
}
const textInput = (placeholder: string) =>
  [...container.querySelectorAll<HTMLInputElement | HTMLTextAreaElement>('input, textarea')].find(
    (node) => visible(node) && node.placeholder === placeholder,
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
async function fillCreateForm() {
  await click('New service')
  for (const [placeholder, value] of [
    ['pole-api', 'sample'],
    ['docker.io', 'docker.io'],
    ['library/nginx', 'library/nginx'],
  ]) {
    await fill(textInput(placeholder), value)
  }
  const selector = [...container.querySelectorAll('select')].find(
    (select) => visible(select) && select.querySelector(`option[value="${group.id}"]`),
  )!
  await act(async () => {
    selector.value = group.id
    selector.dispatchEvent(new Event('change', { bubbles: true }))
  })
}
async function submit() {
  await act(async () => {
    ;[...container.querySelectorAll('form')]
      .find(visible)!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
  })
}

it('shows backend field validation beside editable service input and retains the failed draft', async () => {
  const base = fetchMock.getMockImplementation()!
  fetchMock.mockImplementation((url, init) =>
    init?.method === 'POST' && String(url).endsWith('/web-services')
      ? Promise.resolve(
          new Response(
            JSON.stringify({
              title: 'Validation failed',
              errors: {
                'Body.Name': ['Choose a unique service name.'],
                'Body.Spec.Image.Name': ['Image was rejected.'],
              },
            }),
            { status: 400 },
          ),
        )
      : base(url, init),
  )
  await act(async () => render())
  await fillCreateForm()
  await submit()
  expect(textInput('pole-api').value).toBe('sample')
  expect(textInput('pole-api').getAttribute('aria-invalid')).toBe('true')
  expect(textInput('library/nginx').getAttribute('aria-invalid')).toBe('true')
  expect(container.textContent).toContain('Choose a unique service name.')
  expect(container.textContent).toContain('Image was rejected.')
  expect(container.textContent).not.toContain('"errors"')
})

it('lists active isolation scopes by name and sends the chosen identity with tenant headers', async () => {
  const base = fetchMock.getMockImplementation()!
  fetchMock.mockImplementation((url, init) =>
    String(url).endsWith('/isolation-scopes')
      ? json([
          scope('default-id', group.id, true),
          scope('production-id'),
          { ...scope('deleting-id'), name: 'Deleting', lifecycle: 'deleting' },
          scope('foreign-id', 'another-group'),
        ])
      : base(url, init),
  )
  await act(async () => {
    render()
  })
  await fillCreateForm()
  const selector = container.querySelector<HTMLSelectElement>('#new-web-service-isolation-scope')!
  expect([...selector.options].map((option) => option.textContent)).toEqual([
    'Group default',
    'Production',
  ])
  expect(selector.value).toBe('')
  expect(container.textContent).not.toContain('MongoDB ObjectId')
  const scopeRequest = fetchMock.mock.calls.find(([url]) =>
    String(url).endsWith('/resource-groups/group/isolation-scopes'),
  )!
  expect(scopeRequest[1]!.headers).toMatchObject({
    Authorization: 'Bearer test-token',
    'X-Pole-Tenant-ID': 'alpha',
  })
  const targetRequests = () =>
    fetchMock.mock.calls.filter(([url]) => String(url).includes('/deployment-targets/available'))
  expect(new URL(String(targetRequests().at(-1)![0])).searchParams.get('isolationScopeId')).toBe(
    'default-id',
  )
  await act(async () => {
    selector.value = 'production-id'
    selector.dispatchEvent(new Event('change', { bubbles: true }))
  })
  expect(new URL(String(targetRequests().at(-1)![0])).searchParams.get('isolationScopeId')).toBe(
    'production-id',
  )
  await submit()
  const [, request] = fetchMock.mock.calls.find(([, init]) => init?.method === 'POST')!
  expect(JSON.parse(request!.body as string).placement.isolationScopeId).toBe('production-id')
})

it('resets scope selection on a group change and ignores late choices from the previous group', async () => {
  const base = fetchMock.getMockImplementation()!
  let finishOther!: (response: Response) => void
  const pendingOther = new Promise<Response>((resolve) => {
    finishOther = resolve
  })
  fetchMock.mockImplementation((url, init) => {
    const path = new URL(String(url)).pathname
    if (path.endsWith('/resource-groups'))
      return json(page([group, { ...group, id: 'other', name: 'Other group' }]))
    if (path.endsWith('/other/isolation-scopes')) return pendingOther
    if (path.endsWith('/group/isolation-scopes'))
      return json([scope('default-id', group.id, true), scope('production-id')])
    return base(url, init)
  })
  await act(async () => {
    render()
  })
  await fillCreateForm()
  const selector = container.querySelector<HTMLSelectElement>('#new-web-service-isolation-scope')!
  const groupSelector = [...container.querySelectorAll('select')].find((select) =>
    select.querySelector('option[value="other"]'),
  )!
  await act(async () => {
    selector.value = 'production-id'
    selector.dispatchEvent(new Event('change', { bubbles: true }))
  })
  await act(async () => {
    groupSelector.value = 'other'
    groupSelector.dispatchEvent(new Event('change', { bubbles: true }))
  })
  expect(selector.value).toBe('')
  expect(selector.disabled).toBe(true)
  expect(button('Create service').disabled).toBe(true)
  await act(async () => {
    groupSelector.value = group.id
    groupSelector.dispatchEvent(new Event('change', { bubbles: true }))
  })
  await act(async () => {
    finishOther(
      new Response(JSON.stringify([scope('other-id', 'other')]), {
        headers: { 'Content-Type': 'application/json' },
      }),
    )
  })
  expect([...selector.options].map((option) => option.value)).toEqual(['', 'production-id'])
  expect(selector.value).toBe('')
  const request = fetchMock.mock.calls.find(([url]) =>
    String(url).endsWith('/other/isolation-scopes'),
  )!
  expect(request[1]!.signal!.aborted).toBe(true)
})

it('keeps group default usable when named scopes fail to load and lets the user retry', async () => {
  const base = fetchMock.getMockImplementation()!
  let failScopes = true
  fetchMock.mockImplementation((url, init) =>
    String(url).endsWith('/isolation-scopes')
      ? failScopes
        ? Promise.resolve(new Response('Scope list unavailable.', { status: 503 }))
        : json([scope('production-id')])
      : base(url, init),
  )
  await act(async () => {
    render()
  })
  await fillCreateForm()
  expect(container.querySelector('[role="alert"]')?.textContent).toContain(
    'You can use the group default.',
  )
  expect(button('Create service').disabled).toBe(false)
  failScopes = false
  await click('Retry scopes')
  expect(container.querySelector('[role="alert"]')).toBeNull()
  expect(container.querySelector('#new-web-service-isolation-scope')!.textContent).toContain(
    'Production',
  )
})
const visibleText = () => {
  const walker = document.createTreeWalker(container, NodeFilter.SHOW_TEXT)
  const text: string[] = []
  while (walker.nextNode()) {
    if (walker.currentNode.parentElement && visible(walker.currentNode.parentElement))
      text.push(walker.currentNode.textContent ?? '')
  }
  return text.join(' ')
}

async function traverseHistory(delta: number, events = 1) {
  await act(async () => {
    await new Promise<void>((resolve) => {
      const listener = () => {
        if (--events === 0) {
          window.removeEventListener('popstate', listener)
          resolve()
        }
      }
      window.addEventListener('popstate', listener)
      window.history.go(delta)
    })
  })
}

it('opens an exact tenant/resource/section URL before storage, deduplicates tabs and reloads without draft values', async () => {
  localStorage.setItem(
    'pole.web-services.workspace.v1.user.beta',
    JSON.stringify({ tabs: [{ id: service.id, section: 'builds' }], activeId: service.id }),
  )
  window.history.replaceState(
    {},
    '',
    '/?view=web-services&tenant=beta&resource=second&section=configuration',
  )
  await act(async () => {
    render()
  })
  expect(textInput('ASPNETCORE_ENVIRONMENT=Production\nLOG_LEVEL=Information').value).toBe('')
  expect(workspaceTab('worker').getAttribute('aria-selected')).toBe('true')
  const [, request] = fetchMock.mock.calls.find(([url]) =>
    String(url).endsWith('/web-services/second'),
  )!
  expect(request!.headers).toMatchObject({ 'X-Pole-Tenant-ID': 'beta' })
  await fill(
    textInput('ASPNETCORE_ENVIRONMENT=Production\nLOG_LEVEL=Information'),
    'URL_DRAFT=private',
  )
  await click('Deployments')
  expect(new URLSearchParams(window.location.search).get('section')).toBe('deployments')
  await traverseHistory(-1)
  expect(textInput('ASPNETCORE_ENVIRONMENT=Production\nLOG_LEVEL=Information').value).toBe(
    'URL_DRAFT=private',
  )
  await traverseHistory(1)
  expect(visibleText()).toContain('Physical deployments')
  expect(
    container.querySelectorAll('#web-services-workspace > [role="tablist"] [role="tab"]'),
  ).toHaveLength(3)
  expect(localStorage.getItem('pole.web-services.workspace.v1.user.beta')).not.toContain(
    'URL_DRAFT',
  )
  await act(async () => {
    root.unmount()
    root = createRoot(container)
    render()
  })
  expect(visibleText()).toContain('Physical deployments')
  await click('Configuration')
  expect(textInput('ASPNETCORE_ENVIRONMENT=Production\nLOG_LEVEL=Information').value).toBe('')
})

it('keeps Back/Forward entries when cancelling departure from a dirty view', async () => {
  created = true
  await act(async () => {
    render()
  })
  await click('Resource groups')
  await click('Web services')
  await click('sample')
  await click('Configuration')
  // Reload keeps the history index, so cancellation traverses back to the right entry.
  await act(async () => {
    root.unmount()
    root = createRoot(container)
    render()
  })
  await fill(textInput('ASPNETCORE_ENVIRONMENT=Production\nLOG_LEVEL=Information'), 'KEEP=true')
  const currentUrl = window.location.href
  const length = window.history.length
  const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
  await traverseHistory(-3, 2)
  expect(window.location.href).toBe(currentUrl)
  expect(window.history.length).toBe(length)
  expect(textInput('ASPNETCORE_ENVIRONMENT=Production\nLOG_LEVEL=Information').value).toBe(
    'KEEP=true',
  )
  confirm.mockReturnValue(true)
  await traverseHistory(-3)
  expect(visibleText()).toContain('Group provider')
  await traverseHistory(1)
  expect(visibleText()).toContain('All web services')
  confirm.mockRestore()
})

it.each(['missing-tenant', ''])(
  'does not issue resource reads for an unavailable URL tenant %s',
  async (tenant) => {
    window.history.replaceState({}, '', `/?view=web-services&tenant=${tenant}&resource=service`)
    await act(async () => {
      render()
    })
    expect(visibleText()).toContain('This tenant is unavailable to your account.')
    expect(fetchMock).not.toHaveBeenCalled()
    expect(selectTenant).not.toHaveBeenCalled()
    const selector = container.querySelector<HTMLSelectElement>('.tenant-selector select')!
    expect(selector.value).toBe('')
    await act(async () => {
      selector.value = 'alpha'
      selector.dispatchEvent(new Event('change', { bubbles: true }))
    })
    expect(visibleText()).toContain('All web services')
    expect(new URLSearchParams(window.location.search).get('tenant')).toBe('alpha')
  },
)

it('paginates and searches across the server inventory, restoring the page through history', async () => {
  const base = fetchMock.getMockImplementation()!
  fetchMock.mockImplementation((url, init) => {
    const request = new URL(String(url))
    if (request.pathname.endsWith('/web-services') && !init?.method) {
      const number = Number(request.searchParams.get('pageNumber'))
      const size = Number(request.searchParams.get('pageSize'))
      const search = request.searchParams.get('search') ?? ''
      const all = [...records.values()].filter((item) => item.name.includes(search))
      return json({
        items: all.slice((number - 1) * size, number * size),
        totalCount: all.length,
        pageNumber: number,
        pageSize: size,
      })
    }
    return base(url, init)
  })
  window.history.replaceState(
    {},
    '',
    '/?view=web-services&tenant=alpha&resource=catalog&pageSize=1',
  )
  await act(async () => {
    render()
  })
  expect(visibleText()).toContain('Page 1 of 2')
  await click('Next page')
  expect(visibleText()).toContain('worker')
  expect(visibleText()).not.toContain('sample')
  expect(new URLSearchParams(window.location.search).get('page')).toBe('2')
  await traverseHistory(-1)
  expect(visibleText()).toContain('Page 1 of 2')
  await traverseHistory(1)
  expect(visibleText()).toContain('Page 2 of 2')
  await fill(textInput('Find by name or service ID'), 'sample')
  await click('Search')
  expect(visibleText()).toContain('Page 1 of 1')
  expect(visibleText()).toContain('sample')
  const [url, init] = fetchMock.mock.calls
    .filter(([url]) => new URL(String(url)).pathname.endsWith('/web-services'))
    .at(-1)!
  expect(new URL(String(url)).searchParams.get('search')).toBe('sample')
  expect(init!.headers).toMatchObject({
    Authorization: 'Bearer test-token',
    'X-Pole-Tenant-ID': 'alpha',
  })
  // Search has fewer pages; its old count must not clamp the page restored by Back.
  await traverseHistory(-1)
  expect(visibleText()).toContain('Page 2 of 2')
  expect(visibleText()).toContain('worker')
  expect(textInput('Find by name or service ID').value).toBe('')
  await traverseHistory(1)
  expect(visibleText()).toContain('Page 1 of 1')
  expect(textInput('Find by name or service ID').value).toBe('sample')
})

it('ignores a late prior-page response and keeps loaded results when refresh fails', async () => {
  const base = fetchMock.getMockImplementation()!
  let finish!: (response: Response) => void
  const pending = new Promise<Response>((resolve) => {
    finish = resolve
  })
  let fail = false
  fetchMock.mockImplementation((url, init) => {
    const request = new URL(String(url))
    if (request.pathname.endsWith('/web-services') && !init?.method) {
      if (fail)
        return Promise.resolve(new Response('Inventory temporarily unavailable.', { status: 503 }))
      if (request.searchParams.get('pageNumber') === '2') return pending
      return json({ items: [service], totalCount: 2, pageNumber: 1, pageSize: 1 })
    }
    return base(url, init)
  })
  window.history.replaceState(
    {},
    '',
    '/?view=web-services&tenant=alpha&resource=catalog&pageSize=1',
  )
  await act(async () => {
    render()
  })
  await click('Next page')
  const [, request] = fetchMock.mock.calls.find(([url]) => String(url).includes('pageNumber=2'))!
  await click('Previous page')
  expect(request!.signal!.aborted).toBe(true)
  await act(async () => {
    finish(
      new Response(
        JSON.stringify({ items: [secondService], totalCount: 2, pageNumber: 2, pageSize: 1 }),
      ),
    )
  })
  expect(visibleText()).not.toContain('worker')
  fail = true
  await click('Refresh web services')
  expect(visibleText()).toContain('Inventory temporarily unavailable.')
  expect(visibleText()).toContain('sample')
  fail = false
  await click('Retry inventory')
  expect(visibleText()).not.toContain('Inventory temporarily unavailable.')
})

it('clamps a stale empty page and rejects an invalid resource link safely', async () => {
  window.history.replaceState({}, '', '/?view=web-services&tenant=alpha&resource=catalog&page=99')
  await act(async () => {
    render()
  })
  expect(new URLSearchParams(window.location.search).get('page')).toBe('1')
  await act(async () => {
    window.history.pushState({}, '', '/?view=web-services&tenant=alpha&resource=')
    window.dispatchEvent(new PopStateEvent('popstate'))
  })
  expect(visibleText()).toContain('This service link is invalid.')
  await click('Open service catalog')
  expect(visibleText()).toContain('All web services')
})

it('aborts a pending inventory on tenant change and does not accept its late response', async () => {
  const base = fetchMock.getMockImplementation()!
  let finish!: (response: Response) => void
  const pending = new Promise<Response>((resolve) => {
    finish = resolve
  })
  fetchMock.mockImplementation((url, init) => {
    if (new URL(String(url)).pathname.endsWith('/web-services') && !init?.method) {
      return (init!.headers as Record<string, string>)['X-Pole-Tenant-ID'] === 'alpha'
        ? pending
        : json(page([secondService]))
    }
    return base(url, init)
  })
  await act(async () => {
    render()
  })
  const [, oldRequest] = fetchMock.mock.calls.find(([url]) =>
    new URL(String(url)).pathname.endsWith('/web-services'),
  )!
  const selector = container.querySelector<HTMLSelectElement>('.tenant-selector select')!
  await act(async () => {
    selector.value = 'beta'
    selector.dispatchEvent(new Event('change', { bubbles: true }))
  })
  expect(oldRequest!.signal!.aborted).toBe(true)
  expect(visibleText()).toContain('worker')
  await act(async () => {
    finish(new Response(JSON.stringify(page([service]))))
  })
  expect(visibleText()).not.toContain('sample')
  expect(new URLSearchParams(window.location.search).get('tenant')).toBe('beta')
})

it('retains mounted drafts while reporting an invalid history link', async () => {
  created = true
  await act(async () => {
    render()
  })
  await click('sample')
  await click('Configuration')
  await fill(textInput('latest'), 'retain-invalid-link-draft')
  await act(async () => {
    window.history.pushState({}, '', '/?view=web-services&tenant=alpha&resource=')
    window.dispatchEvent(new PopStateEvent('popstate'))
  })
  expect(visibleText()).toContain('This service link is invalid.')
  expect(visibleText()).not.toContain('Service configuration')
  await selectTab('sample')
  expect(textInput('latest').value).toBe('retain-invalid-link-draft')
})

it('does not expose a creation form through a direct URL for a Viewer', async () => {
  window.history.replaceState({}, '', '/?view=web-services&tenant=alpha&resource=create')
  await act(async () => {
    render('alpha', 'Viewer')
  })
  expect(visibleText()).toContain('Tenant viewer access is read-only.')
  expect(container.querySelector('form:not(.inventory-search)')).toBeNull()
})

it('keeps tabs and drafts opened while a creation request is completing', async () => {
  created = true
  let finish!: (response: Response) => void
  pendingCreate = new Promise<Response>((resolve) => {
    finish = resolve
  })
  await act(async () => {
    render()
  })
  await fillCreateForm()
  await submit()
  await selectTab('All services')
  await click('worker')
  await click('Configuration')
  await fill(textInput('latest'), 'opened-during-create')
  await act(async () => {
    finish(
      new Response(JSON.stringify({ webServiceId: service.id, deployments: service.deployments })),
    )
  })
  expect(visibleText()).toContain('Service overview')
  await selectTab('worker')
  expect(textInput('latest').value).toBe('opened-during-create')
})

it('returns to the previous page when its last service is deleted', async () => {
  const base = fetchMock.getMockImplementation()!
  fetchMock.mockImplementation((url, init) => {
    const request = new URL(String(url))
    if (
      request.pathname.endsWith(`/web-services/${secondService.id}`) &&
      init?.method === 'DELETE'
    ) {
      records.delete(secondService.id)
      return Promise.resolve(new Response(null, { status: 204 }))
    }
    if (request.pathname.endsWith('/web-services') && !init?.method) {
      const pageNumber = Number(request.searchParams.get('pageNumber'))
      return json({
        items: [...records.values()].slice(pageNumber - 1, pageNumber),
        totalCount: records.size,
        pageNumber,
        pageSize: 1,
      })
    }
    return base(url, init)
  })
  window.history.replaceState(
    {},
    '',
    '/?view=web-services&tenant=alpha&resource=catalog&page=2&pageSize=1',
  )
  await act(async () => {
    render()
  })
  await click('Delete worker')
  await click('Delete service')
  expect(visibleText()).toContain('Page 1 of 1')
  expect(visibleText()).toContain('sample')
  expect(visibleText()).not.toContain('worker')
  expect(new URLSearchParams(window.location.search).get('page')).toBe('1')
})

it('keeps the catalog separate, then creates an image with auth/tenant headers and opens Overview', async () => {
  await act(async () => {
    render()
  })
  expect(container.querySelector('form:not(.inventory-search)')).toBeNull()
  await fillCreateForm()
  await submit()
  const [, request] = fetchMock.mock.calls.find(([, init]) => init?.method === 'POST')!
  expect(request!.headers).toMatchObject({
    Authorization: 'Bearer test-token',
    'X-Pole-Tenant-ID': 'alpha',
  })
  expect(JSON.parse(request!.body as string)).toMatchObject({
    name: 'sample',
    resourceGroupId: group.id,
    exposure: 'Private',
    placement: { targets: [{ deploymentTargetId: target.id, fixedReplicas: 1 }] },
  })
  expect(visibleText()).toContain('Service overview')
  expect(visibleText()).not.toContain('Physical deployments')
  expect(container.textContent).toContain('Service configuration accepted.')
  expect(workspaceTab('New service')).toBeUndefined()
  await click('Deployments')
  expect(visibleText()).toContain('Physical deployments')
  expect(visibleText()).toContain('PendingApply')
})

it('preserves a rejected creation draft and its concrete API failure', async () => {
  failCreate = true
  await act(async () => {
    render()
  })
  await fillCreateForm()
  await submit()
  expect(container.querySelector('[role="alert"]')?.textContent).toContain(
    'This name already exists.',
  )
  expect(textInput('pole-api').value).toBe('sample')
  expect(container.textContent).not.toContain('Service configuration accepted.')
})

it('retains separate service drafts while switching resource tabs and sections', async () => {
  created = true
  await act(async () => {
    render()
  })
  await click('sample')
  await click('Configuration')
  await fill(textInput('latest'), 'draft-first')
  await click('Overview')
  expect(visibleText()).not.toContain('Service configuration')
  await selectTab('All services')
  await click('worker')
  await click('Configuration')
  await fill(textInput('latest'), 'draft-second')
  await selectTab('sample')
  await click('Configuration')
  expect(textInput('latest').value).toBe('draft-first')
  await selectTab('worker')
  expect(textInput('latest').value).toBe('draft-second')
  expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'PUT')).toHaveLength(0)
})

it('cancels dirty-tab closure and discards only after confirmation', async () => {
  await act(async () => {
    render()
  })
  await fillCreateForm()
  await selectTab('All services')
  await selectTab('New service')
  expect(textInput('pole-api').value).toBe('sample')
  await click('Close New service')
  expect(container.querySelector('dialog')?.textContent).toContain(
    'Your unsaved changes will be lost.',
  )
  await click('Cancel')
  expect(textInput('pole-api').value).toBe('sample')
  await click('Close New service')
  await click('Discard and close')
  expect(container.querySelector('form:not(.inventory-search)')).toBeNull()
  expect(workspaceTab('All services').getAttribute('aria-selected')).toBe('true')
})

it('guards module navigation, tenant selection and browser unload without persisting form values', async () => {
  await act(async () => {
    render()
  })
  await fillCreateForm()
  await click('Resource groups')
  expect(container.querySelector('dialog')?.textContent).toContain('Discard unsaved changes?')
  await click('Cancel')
  const selector = container.querySelector<HTMLSelectElement>('.top-band select')!
  await act(async () => {
    selector.value = 'beta'
    selector.dispatchEvent(new Event('change', { bubbles: true }))
  })
  expect(selectTenant).not.toHaveBeenCalled()
  await click('Cancel')
  const unload = new Event('beforeunload', { cancelable: true })
  window.dispatchEvent(unload)
  expect(unload.defaultPrevented).toBe(true)
  expect(localStorage.getItem(storageKey)).not.toContain('test-token')
  expect(localStorage.getItem(storageKey)).not.toContain('sample')
  await click('Resource groups')
  await click('Discard and continue')
  expect(container.textContent).toContain('Group provider')
})

it('saves configuration, clears the dirty guard and restores only server data after remount', async () => {
  created = true
  await act(async () => {
    render()
  })
  await click('sample')
  await click('Configuration')
  await fill(textInput('latest'), 'saved-tag')
  await submit()
  const [, request] = fetchMock.mock.calls.find(([, init]) => init?.method === 'PUT')!
  expect(request!.headers).toMatchObject({
    Authorization: 'Bearer test-token',
    'X-Pole-Tenant-ID': 'alpha',
  })
  expect(workspaceTab('sample').getAttribute('aria-label')).toBe('sample')
  const unload = new Event('beforeunload', { cancelable: true })
  window.dispatchEvent(unload)
  expect(unload.defaultPrevented).toBe(false)
  expect(JSON.parse(localStorage.getItem(storageKey)!)).toEqual({
    tabs: [{ id: 'service', section: 'configuration' }],
    activeId: 'service',
  })
  await act(async () => {
    root.unmount()
  })
  root = createRoot(container)
  await act(async () => {
    render()
  })
  expect(textInput('latest').value).toBe('saved-tag')
  expect(workspaceTab('sample').getAttribute('aria-selected')).toBe('true')
})

it('drops a pending navigation confirmation when an external session change removes its draft', async () => {
  await act(async () => {
    render()
  })
  await fillCreateForm()
  await click('Resource groups')
  expect(container.querySelector('dialog')).not.toBeNull()
  await act(async () => {
    render('alpha', 'Owner', false)
  })
  await act(async () => {
    render()
  })
  expect(container.querySelector('dialog')).toBeNull()
  await click('New service')
  await fill(textInput('pole-api'), 'fresh-draft')
  expect(container.querySelector('dialog')).toBeNull()
})

it('keeps an update draft dirty when the server rejects it', async () => {
  created = true
  failUpdate = true
  await act(async () => {
    render()
  })
  await click('sample')
  await click('Configuration')
  await fill(textInput('latest'), 'rejected-tag')
  await submit()
  expect(container.querySelector('[role="alert"]')?.textContent).toContain('Configuration changed.')
  expect(textInput('latest').value).toBe('rejected-tag')
  expect(workspaceTab('sample').getAttribute('aria-label')).toContain('unsaved changes')
})

it('refreshes a clean configuration while preserving a dirty draft across newer server revisions', async () => {
  created = true
  await act(async () => {
    render()
  })
  await click('sample')
  await click('Configuration')
  records.set(service.id, {
    ...service,
    activeRevision: 2,
    spec: { ...service.spec, image: { ...service.spec.image!, tag: 'server-two' } },
  })
  await click('Refresh service details')
  expect(textInput('latest').value).toBe('server-two')
  expect(workspaceTab('sample').getAttribute('aria-label')).toBe('sample')
  await fill(textInput('latest'), 'keep-my-draft')
  records.set(service.id, {
    ...service,
    activeRevision: 3,
    spec: { ...service.spec, image: { ...service.spec.image!, tag: 'server-three' } },
  })
  await click('Refresh service details')
  expect(textInput('latest').value).toBe('keep-my-draft')
  expect(workspaceTab('sample').getAttribute('aria-label')).toContain('unsaved changes')
  await click('Discard changes')
  await click('Discard changes')
  expect(textInput('latest').value).toBe('server-three')
  expect(workspaceTab('sample').getAttribute('aria-label')).toBe('sample')
  expect(document.activeElement?.getAttribute('id')).toBe('web-service-service-tab-configuration')
})

it('protects a newer build from a stale configuration save and adopts its revision only after discarding', async () => {
  created = true
  records.set(service.id, { ...service, activeRevision: 2 })
  await act(async () => {
    render()
  })
  await click('sample')
  await click('Configuration')
  await fill(textInput('latest'), 'old-draft')
  records.set(service.id, {
    ...service,
    activeRevision: 3,
    spec: { ...service.spec, image: { ...service.spec.image!, tag: 'new-build' } },
  })
  await click('Refresh service details')
  expect(visibleText()).toContain('The service changed while this draft was open.')
  await submit()
  expect(records.get(service.id)?.spec.image?.tag).toBe('new-build')
  expect(records.get(service.id)?.activeRevision).toBe(3)
  expect(container.querySelector('[role="alert"]')?.textContent).toContain('Configuration changed.')
  expect(textInput('latest').value).toBe('old-draft')
  expect(workspaceTab('sample').getAttribute('aria-label')).toContain('unsaved changes')
  const updates = () => fetchMock.mock.calls.filter(([, init]) => init?.method === 'PUT')
  expect(JSON.parse(updates()[0][1]!.body as string).expectedRevision).toBe(2)

  await click('Discard changes')
  await click('Discard changes')
  expect(textInput('latest').value).toBe('new-build')
  expect(visibleText()).not.toContain('The service changed while this draft was open.')
  await fill(textInput('latest'), 'reviewed-change')
  await submit()
  expect(JSON.parse(updates()[1][1]!.body as string).expectedRevision).toBe(3)
  expect(records.get(service.id)?.activeRevision).toBe(4)
  expect(records.get(service.id)?.spec.image?.tag).toBe('reviewed-change')
  expect(workspaceTab('sample').getAttribute('aria-label')).toBe('sample')
})

it('loads restored resource references directly and reports missing resources without opening a form', async () => {
  localStorage.setItem(
    storageKey,
    JSON.stringify({ tabs: [{ id: 'missing', section: 'configuration' }], activeId: 'missing' }),
  )
  await act(async () => {
    render()
  })
  expect(visibleText()).toContain('Unable to open this service.')
  expect(container.querySelector('form:not(.inventory-search)')).toBeNull()
  const [, request] = fetchMock.mock.calls.find(([url]) =>
    String(url).endsWith('/web-services/missing'),
  )!
  expect(request!.headers).toMatchObject({
    Authorization: 'Bearer test-token',
    'X-Pole-Tenant-ID': 'alpha',
  })
})

it('restores a service outside the catalog page and stores no configuration draft', async () => {
  localStorage.setItem(
    storageKey,
    JSON.stringify({ tabs: [{ id: 'service', section: 'configuration' }], activeId: 'service' }),
  )
  await act(async () => {
    render()
  })
  expect(textInput('latest').value).toBe('stable')
  expect(workspaceTab('sample').getAttribute('aria-selected')).toBe('true')
  await fill(
    textInput('ASPNETCORE_ENVIRONMENT=Production\nLOG_LEVEL=Information'),
    'DRAFT_ONLY=keep-in-memory',
  )
  expect(localStorage.getItem(storageKey)).not.toContain('DRAFT_ONLY')
  expect(localStorage.getItem(storageKey)).not.toContain('keep-in-memory')
  await act(async () => {
    render('beta')
  })
  expect(workspaceTab('sample')).toBeUndefined()
  expect(JSON.parse(localStorage.getItem(storageKey)!)).toEqual({
    tabs: [{ id: 'service', section: 'configuration' }],
    activeId: 'service',
  })
})

it('allows a viewer to inspect each section without showing mutation forms', async () => {
  created = true
  await act(async () => {
    render('alpha', 'Viewer')
  })
  expect(button('New service').disabled).toBe(true)
  await click('Inspect')
  await click('Configuration')
  expect(visibleText()).toContain('Tenant viewer access is read-only.')
  expect(container.querySelector('form:not(.inventory-search)')).toBeNull()
  await click('Deployments')
  expect(visibleText()).toContain('Physical deployments')
  expect(button('Retry').disabled).toBe(true)
})

it('does not restore old-tenant tabs or announce an old-tenant creation completion', async () => {
  let resolveCreate!: (response: Response) => void
  pendingCreate = new Promise<Response>((resolve) => {
    resolveCreate = resolve
  })
  await act(async () => {
    render()
  })
  await fillCreateForm()
  await submit()
  await act(async () => {
    render('beta')
  })
  await act(async () => {
    resolveCreate(new Response(JSON.stringify({ webServiceId: service.id })))
  })
  expect(container.textContent).not.toContain('Service configuration accepted.')
  expect(workspaceTab('sample')).toBeUndefined()
  expect(container.querySelector('form:not(.inventory-search)')).toBeNull()
})

it('does not reopen a creation tab when a mutation completes after the draft was closed', async () => {
  let resolveCreate!: (response: Response) => void
  pendingCreate = new Promise<Response>((resolve) => {
    resolveCreate = resolve
  })
  await act(async () => {
    render()
  })
  await fillCreateForm()
  await submit()
  await click('Close New service')
  await click('Discard and close')
  await act(async () => {
    resolveCreate(new Response(JSON.stringify({ webServiceId: service.id })))
  })
  expect(workspaceTab('sample')).toBeUndefined()
  expect(workspaceTab('All services').getAttribute('aria-selected')).toBe('true')
})

it('moves tab focus with arrow keys and uses Delete to request dirty-tab closure', async () => {
  created = true
  await act(async () => {
    render()
  })
  await click('sample')
  await click('Configuration')
  await fill(textInput('latest'), 'keyboard-draft')
  workspaceTab('sample').focus()
  await act(async () => {
    workspaceTab('sample').dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Home', bubbles: true }),
    )
  })
  expect(document.activeElement).toBe(workspaceTab('All services'))
  await act(async () => {
    workspaceTab('All services').dispatchEvent(
      new KeyboardEvent('keydown', { key: 'End', bubbles: true }),
    )
  })
  expect(document.activeElement).toBe(workspaceTab('sample'))
  await act(async () => {
    workspaceTab('sample').dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Delete', bubbles: true }),
    )
  })
  expect(container.querySelector('dialog')?.textContent).toContain('Discard changes to sample?')
})

const authoringPlan = {
  candidateKey: 'docker',
  kind: 'Dockerfile',
  displayName: 'Dockerfile',
  manifestPath: 'Dockerfile',
  sourceRoot: '.',
  confidence: 'High',
  signals: [],
  warnings: [],
  commands: [],
  dockerfilePreview: 'FROM scratch',
}
const gitSource = {
  id: 'source',
  technicalName: 'application',
  revision: 1,
  connectionId: 'connection',
  externalRepositoryId: 'repository',
  repositoryFullName: 'team/app',
  repositoryUrl: 'https://github.com/team/app',
  isPrivate: true,
  branch: 'main',
}
const buildConfiguration = {
  id: 'configuration',
  technicalName: 'app-build',
  revision: 1,
  webServiceId: service.id,
  sourceRepositoryId: gitSource.id,
  recipe: { dockerfile: 'FROM scratch' },
}
async function fillBuildRecipe() {
  const field = (label: string) =>
    [...container.querySelectorAll('label')]
      .find((item) => visible(item) && item.textContent?.includes(label))!
      .querySelector<HTMLInputElement | HTMLTextAreaElement>('input,textarea')!
  await fill(field('Build configuration technical name'), 'app-build')
  await fill(field('Dockerfile'), 'FROM scratch')
}
async function chooseGitSource() {
  await click('Git repository')
  const selector = [...container.querySelectorAll('select')].find(
    (select) => visible(select) && select.querySelector('option[value="source"]'),
  )!
  await act(async () => {
    selector.value = 'source'
    selector.dispatchEvent(new Event('change', { bubbles: true }))
  })
  await fillBuildRecipe()
}
it('recovers accepted source configuration after a lost response without creating another service or configuration', async () => {
  const base = fetchMock.getMockImplementation()!
  let configurationAccepted = false
  fetchMock.mockImplementation((url, init) => {
    const path = new URL(String(url), 'http://localhost').pathname
    if (path.endsWith('/source-repositories')) return json([gitSource])
    if (path.endsWith('/source-build-configurations')) {
      if (init?.method === 'POST') {
        configurationAccepted = true
        return Promise.resolve(
          new Response('Connection lost after configuration acceptance.', { status: 503 }),
        )
      }
      return json(configurationAccepted ? [buildConfiguration] : [])
    }
    if (path.endsWith('/builds') && init?.method === 'POST')
      return json({
        id: 'build',
        configurationId: 'configuration',
        webServiceId: service.id,
        branch: 'main',
        commitSha: 'abc123456789',
        createdAtUtc: '2026-10-03T00:00:00Z',
        state: { status: 'queued' },
      })
    return base(url, init)
  })
  await act(async () => render())
  await fillCreateForm()
  await chooseGitSource()
  await submit()
  expect(container.textContent).toContain('Your service is retained.')
  expect(container.textContent).toContain('Connection lost after configuration acceptance.')
  await click('Retry source setup')
  expect(new URLSearchParams(window.location.search).get('section')).toBe('builds')
  expect(
    fetchMock.mock.calls.filter(
      ([url, init]) => String(url).endsWith('/web-services') && init?.method === 'POST',
    ),
  ).toHaveLength(1)
  expect(
    fetchMock.mock.calls.filter(
      ([url, init]) =>
        String(url).endsWith('/source-build-configurations') && init?.method === 'POST',
    ),
  ).toHaveLength(1)
  expect(
    fetchMock.mock.calls.filter(
      ([url, init]) => String(url).endsWith('/builds') && init?.method === 'POST',
    ),
  ).toHaveLength(1)
  expect(container.textContent).toContain(
    'A successful build does not establish application readiness.',
  )
})
it('does not continue a multi-step Git mutation after the originating tenant workspace is unmounted', async () => {
  const base = fetchMock.getMockImplementation()!
  let resolveConfigurations!: (value: Response) => void
  const pending = new Promise<Response>((resolve) => {
    resolveConfigurations = resolve
  })
  fetchMock.mockImplementation((url, init) => {
    const path = new URL(String(url), 'http://localhost').pathname
    if (path.endsWith('/source-repositories')) return json([gitSource])
    if (path.endsWith('/source-build-configurations') && !init?.method) return pending
    return base(url, init)
  })
  await act(async () => render())
  await fillCreateForm()
  await chooseGitSource()
  await submit()
  expect(container.textContent).toContain('Service created')
  const tenant =
    container.querySelector<HTMLSelectElement>('#active-tenant') ??
    [...container.querySelectorAll('select')].find((item) =>
      item.querySelector('option[value="beta"]'),
    )!
  await act(async () => {
    tenant.value = 'beta'
    tenant.dispatchEvent(new Event('change', { bubbles: true }))
  })
  await act(async () =>
    resolveConfigurations(new Response('[]', { headers: { 'Content-Type': 'application/json' } })),
  )
  expect(
    fetchMock.mock.calls.some(
      ([url, init]) =>
        String(url).endsWith('/source-build-configurations') && init?.method === 'POST',
    ),
  ).toBe(false)
  expect(
    fetchMock.mock.calls.some(
      ([url, init]) => String(url).endsWith('/builds') && init?.method === 'POST',
    ),
  ).toBe(false)
  expect(container.textContent).not.toContain('Git build queued.')
})
it('keeps the Git creation form disabled until a source is selected and retains the image draft when changing source modes', async () => {
  const base = fetchMock.getMockImplementation()!
  fetchMock.mockImplementation((url, init) =>
    String(url).endsWith('/source-repositories') ? json([gitSource]) : base(url, init),
  )
  await act(async () => render())
  await fillCreateForm()
  await click('Git repository')
  expect(button('Create service and build').disabled).toBe(true)
  expect(textInput('library/nginx')).toBeUndefined()
  expect(container.textContent).toContain('The service waits for its first successful build.')
  await click('Container image')
  expect(button('Create service').disabled).toBe(false)
  expect(textInput('pole-api').value).toBe('sample')
  expect(textInput('library/nginx').value).toBe('library/nginx')
})

it('links a named repository and copies optional analysis into an explicit build recipe', async () => {
  const base = fetchMock.getMockImplementation()!
  let linked = false
  fetchMock.mockImplementation((url, init) => {
    const path = new URL(String(url), 'http://localhost').pathname
    if (path.endsWith('/git-provider-connections'))
      return json([{ id: 'connection', accountLogin: 'team' }])
    if (path.endsWith('/source-repositories')) return json(linked ? [gitSource] : [])
    if (path.endsWith('/repositories'))
      return json({
        items: [{ id: 'repository', fullName: 'team/app', defaultBranch: 'main' }],
        hasMore: false,
      })
    if (path.endsWith('/branches'))
      return json({ items: [{ name: 'main' }, { name: 'release' }], hasMore: false })
    if (path.endsWith('/analysis'))
      return json({
        repositoryFullName: 'team/app',
        commitSha: 'abc123456789',
        candidates: [
          authoringPlan,
          { ...authoringPlan, candidateKey: 'node', displayName: 'Node application' },
        ],
      })
    if (path.endsWith('/links') && init?.method === 'POST') {
      linked = true
      return json(gitSource)
    }
    return base(url, init)
  })
  await act(async () => render())
  await fillCreateForm()
  await click('Git repository')
  const select = async (value: string) => {
    const input = [...container.querySelectorAll('select')].find(
      (item) => visible(item) && item.querySelector(`option[value="${value}"]`),
    )!
    await act(async () => {
      input.value = value
      input.dispatchEvent(new Event('change', { bubbles: true }))
    })
  }
  await select('repository')
  await select('release')
  await click('Analyze branch')
  const nameInput = [...container.querySelectorAll('label')]
    .find((item) => visible(item) && item.textContent?.includes('Repository technical name'))!
    .querySelector('input')!
  await fill(nameInput, 'application')
  await select('docker')
  await click('Use this source')
  await fillBuildRecipe()
  expect(button('Create service and build').disabled).toBe(false)
  const link = fetchMock.mock.calls.find(
    ([url, init]) => String(url).endsWith('/links') && init?.method === 'POST',
  )!
  expect(JSON.parse(String(link[1]?.body))).toEqual({
    branch: 'release',
    technicalName: 'application',
  })
  expect(container.textContent).toContain('team/app')
})

it('preserves a dirty configuration and selected tab across token rotation, then uses the current credential', async () => {
  created = true
  await act(async () => render())
  await click('sample')
  await click('Configuration')
  await fill(textInput('latest'), 'token-refresh-draft')
  const url = window.location.href
  await act(async () => render('alpha', 'Owner', true, 'refreshed-token'))
  expect(textInput('latest').value).toBe('token-refresh-draft')
  expect(workspaceTab('sample').getAttribute('aria-selected')).toBe('true')
  expect(window.location.href).toBe(url)
  expect(container.querySelector('dialog')).toBeNull()
  await click('Refresh service details')
  expect(textInput('latest').value).toBe('token-refresh-draft')
  expect(
    fetchMock.mock.calls
      .filter(([url]) => String(url).endsWith('/web-services/service'))
      .at(-1)?.[1]?.headers,
  ).toMatchObject({ Authorization: 'Bearer refreshed-token', 'X-Pole-Tenant-ID': 'alpha' })
  await submit()
  expect(
    fetchMock.mock.calls.find(([, init]) => init?.method === 'PUT')?.[1]?.headers,
  ).toMatchObject({ Authorization: 'Bearer refreshed-token' })
})

it('retains the accepted draft and blocks another write until a post-save read recovers', async () => {
  created = true
  await act(async () => render())
  await click('sample')
  await click('Configuration')
  await fill(textInput('latest'), 'accepted-tag')
  const base = fetchMock.getMockImplementation()!
  let unavailable = true
  fetchMock.mockImplementation((url, init) =>
    String(url).endsWith('/web-services/service') && !init?.method && unavailable
      ? Promise.resolve(new Response('Read temporarily unavailable', { status: 503 }))
      : base(url, init),
  )
  await submit()
  expect(textInput('latest').value).toBe('accepted-tag')
  expect(button('Save changes').disabled).toBe(true)
  expect(container.textContent).toContain('Configuration was accepted. Refresh service details')
  expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'PUT')).toHaveLength(1)
  unavailable = false
  await click('Refresh service details')
  expect(textInput('latest').value).toBe('accepted-tag')
  expect(container.textContent).not.toContain('Configuration was accepted. Refresh service details')
  await fill(textInput('latest'), 'next-tag')
  await submit()
  const writes = fetchMock.mock.calls.filter(([, init]) => init?.method === 'PUT')
  expect(JSON.parse(writes.at(-1)![1]!.body as string).expectedRevision).toBe(2)
})

it('reads the accepted revision instead of adopting a pre-save response that finishes late', async () => {
  created = true
  await act(async () => render())
  await click('sample')
  await click('Configuration')
  await fill(textInput('latest'), 'accepted-tag')
  const base = fetchMock.getMockImplementation()!
  let complete!: (value: Response) => void
  const oldRead = new Promise<Response>((resolve) => {
    complete = resolve
  })
  let intercept = true
  let signal: AbortSignal | null | undefined
  fetchMock.mockImplementation((url, init) => {
    if (String(url).endsWith('/web-services/service') && !init?.method && intercept) {
      intercept = false
      signal = init?.signal
      return oldRead
    }
    return base(url, init)
  })
  await click('Refresh service details')
  await submit()
  expect(signal?.aborted).toBe(true)
  await act(async () => complete(await json(service)))
  expect(textInput('latest').value).toBe('accepted-tag')
  await fill(textInput('latest'), 'next-tag')
  await submit()
  const writes = fetchMock.mock.calls.filter(([, init]) => init?.method === 'PUT')
  expect(JSON.parse(writes.at(-1)![1]!.body as string).expectedRevision).toBe(2)
})

it('clears the discarded editor guard and disables mutations when membership becomes Viewer', async () => {
  created = true
  await act(async () => render())
  await click('sample')
  await click('Configuration')
  await fill(textInput('latest'), 'privileged-draft')
  await act(async () => render('alpha', 'Viewer'))
  expect(container.textContent).toContain('Tenant viewer access is read-only.')
  expect(button('Save changes')).toBeUndefined()
  expect(workspaceTab('sample').getAttribute('aria-label')).toBe('sample')
  await click('Resource groups')
  expect(container.querySelector('dialog')).toBeNull()
  expect(container.textContent).toContain('Group provider')
})

it('freezes the selected Git source and delivery mode while the initial create request is pending', async () => {
  const base = fetchMock.getMockImplementation()!
  const otherSource = { ...gitSource, id: 'other-source', repositoryFullName: 'team/other' }
  let resolveCreate!: (response: Response) => void
  pendingCreate = new Promise<Response>((resolve) => {
    resolveCreate = resolve
  })
  fetchMock.mockImplementation((url, init) => {
    const path = new URL(String(url), 'http://localhost').pathname
    if (path.endsWith('/source-repositories')) return json([gitSource, otherSource])
    if (path.endsWith('/source-build-configurations') && init?.method === 'POST')
      return json(buildConfiguration)
    if (path.endsWith('/builds') && init?.method === 'POST')
      return json({ id: 'build', configurationId: 'configuration', webServiceId: service.id })
    return base(url, init)
  })
  await act(async () => render())
  await fillCreateForm()
  await chooseGitSource()
  await submit()
  expect(button('Container image').disabled).toBe(true)
  const selector = [...container.querySelectorAll('select')].find((select) =>
    select.querySelector('option[value="source"]'),
  )!
  expect(selector.disabled).toBe(true)
  expect(selector.value).toBe('source')
  await act(async () => resolveCreate(new Response(JSON.stringify({ webServiceId: service.id }))))
  const write = fetchMock.mock.calls.find(
    ([url, init]) =>
      String(url).endsWith('/source-build-configurations') && init?.method === 'POST',
  )!
  expect(JSON.parse(String(write[1]?.body))).toEqual({
    sourceRepositoryId: 'source',
    webServiceId: service.id,
    technicalName: 'app-build',
    recipe: { dockerfile: 'FROM scratch' },
  })
  const create = fetchMock.mock.calls.find(
    ([url, init]) => String(url).endsWith('/web-services') && init?.method === 'POST',
  )!
  const body = JSON.parse(String(create[1]?.body))
  expect(body.creationMode).toBe('SourceBuild')
  expect(body.spec.image).toBeNull()
  expect(new URLSearchParams(window.location.search).get('section')).toBe('builds')
})

it('recovers an accepted build queue response without queueing the job twice', async () => {
  const base = fetchMock.getMockImplementation()!
  let queued = false
  const job = {
    id: 'build',
    configurationId: 'configuration',
    webServiceId: service.id,
    branch: 'main',
    commitSha: 'abc',
    createdAtUtc: '2026-10-03T00:00:00Z',
    state: { status: 'queued' },
  }
  fetchMock.mockImplementation((url, init) => {
    const path = new URL(String(url), 'http://localhost').pathname
    if (path.endsWith('/source-repositories')) return json([gitSource])
    if (path.endsWith('/source-build-configurations'))
      return json(init?.method === 'POST' ? buildConfiguration : queued ? [buildConfiguration] : [])
    if (path.endsWith('/source-build-jobs')) return json(page(queued ? [job] : []))
    if (path.endsWith('/builds') && init?.method === 'POST') {
      queued = true
      return Promise.resolve(new Response('Queue result unavailable', { status: 503 }))
    }
    return base(url, init)
  })
  await act(async () => render())
  await fillCreateForm()
  await chooseGitSource()
  await submit()
  expect(container.textContent).toContain('Your service is retained.')
  await click('Retry source setup')
  expect(new URLSearchParams(window.location.search).get('section')).toBe('builds')
  expect(
    fetchMock.mock.calls.filter(
      ([url, init]) => String(url).endsWith('/builds') && init?.method === 'POST',
    ),
  ).toHaveLength(1)
})

it('retains the creation draft after a definitive rejection and never repeats an uncertain create', async () => {
  await act(async () => render())
  await fillCreateForm()
  failCreate = true
  await submit()
  expect(textInput('pole-api').value).toBe('sample')
  expect(textInput('library/nginx').value).toBe('library/nginx')
  const base = fetchMock.getMockImplementation()!
  fetchMock.mockImplementation((url, init) =>
    String(url).endsWith('/web-services') && init?.method === 'POST'
      ? Promise.resolve(new Response('Creation result unavailable', { status: 503 }))
      : base(url, init),
  )
  await submit()
  expect(container.textContent).toContain('The creation request will not be repeated.')
  await click('Inspect creation result')
  expect(container.textContent).toContain('The creation result is still unknown.')
  expect(
    fetchMock.mock.calls.filter(
      ([url, init]) => String(url).endsWith('/web-services') && init?.method === 'POST',
    ),
  ).toHaveLength(2)
})

it.each([
  ['Dependencies', 'Deployments'],
  ['Deployments', 'Dependencies'],
])('shares private target choices across %s and %s', async (first, second) => {
  created = true
  const baseline = fetchMock.getMockImplementation()!
  fetchMock.mockImplementation((input, init) => {
    const path = new URL(String(input), 'http://localhost').pathname
    if (path.endsWith('/resource-connections/')) return json(page([]))
    if (path.endsWith('/secrets/')) return json({ items: [], nextCursor: null })
    if (path.endsWith('/resource-groups/group/resources')) return json(page([]))
    return baseline(input, init)
  })
  await act(async () => render())
  await click('sample')
  await click(first)
  await click(second)
  await click(first)
  expect(container.querySelector('[data-testid="deployment-row-deployment"]')).not.toBeNull()
  expect(container.textContent).not.toContain('Unable to render this view')
  expect(container.textContent).toContain('Application dependencies')
})

it('refreshes a mounted service and catalog after an accepted operational retry', async () => {
  created = true
  let retried = false
  records.set(service.id, {
    ...service,
    deployments: [
      { ...service.deployments[0], status: 'Failed', failureCode: 'TargetUnavailable' },
    ],
  })
  const baseline = fetchMock.getMockImplementation()!
  fetchMock.mockImplementation((input, init) => {
    const path = new URL(String(input), 'http://localhost').pathname
    if (path.endsWith('/resource-deployments/operations')) {
      const placement = records.get(service.id)!.deployments[0]
      return json({
        ...page([
          {
            deployment: {
              id: placement.deploymentId,
              resourceType: 'pole/web-service',
              resourceId: service.id,
              placement,
            },
            phase: retried ? 'Accepted' : 'Failed',
            retryable: true,
            sinceUtc: '2026-01-01T00:00:00Z',
            attemptCount: 1,
          },
        ]),
        checkedAtUtc: new Date().toISOString(),
        counts: {
          waiting: 0,
          accepted: retried ? 1 : 0,
          running: 0,
          applied: 0,
          failed: retried ? 0 : 1,
        },
      })
    }
    if (path.endsWith('/deployments/deployment/retry')) {
      retried = true
      records.set(service.id, {
        ...service,
        deployments: [{ ...service.deployments[0], status: 'Pending', deploymentRevision: 2 }],
      })
      return json({ id: 'deployment' }, 202)
    }
    return baseline(input, init)
  })
  await act(async () => render())
  await click('sample')
  await click('Deployments')
  expect(container.querySelector('[data-testid="deployment-status"]')?.textContent).toBe('Failed')
  await click('Operations')
  await click('Retry this apply')
  await click('Retry selected operation')
  await click('Deployments')
  expect(container.querySelector('[data-testid="deployment-status"]')?.textContent).toBe('Pending')
  await click('All services')
  expect(
    fetchMock.mock.calls.some(
      ([input, init]) =>
        String(input).includes('/deployments/deployment/retry') && init?.method === 'POST',
    ),
  ).toBe(true)
})

it('keeps Git-owned configuration read-only with source and blocker information', async () => {
  records.set(service.id, {
    ...service,
    gitOps: {
      sourceId: 'source',
      sourceUrl: 'https://github.com/lsbV/example',
      blocker: 'SecretValuesMissing',
      pendingRemoval: false,
      configurationApplied: false,
    },
  })
  created = true
  window.history.replaceState(
    {},
    '',
    `/?view=web-services&resource=${service.id}&section=configuration`,
  )
  await act(async () => render())
  expect(visibleText()).toContain('Git manages this configuration')
  expect(visibleText()).toContain('SecretValuesMissing')
  expect(visibleText()).toContain('Edit this configuration in Git')
  expect([...container.querySelectorAll('form')].filter(visible)).toHaveLength(0)
  expect(visibleText()).toMatch(/Source\s+source/)
  expect(container.querySelector('a[href="https://github.com/lsbV/example"]')).toBeNull()
  expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'PUT')).toBe(false)
})

it('renders a configured service awaiting its first artifact without a placeholder image or active configuration actions', async () => {
  created = true
  records.set(service.id, {
    ...service,
    lifecycle: 'AwaitingFirstArtifact',
    spec: {
      ...service.spec,
      image: null,
      imageAuthority: {
        mode: 'PendingSourceBuild',
        configurationId: 'configuration',
        jobId: null,
        generation: null,
        digest: null,
      },
    },
  })
  await act(async () => render())
  await click('sample')
  expect(container.textContent).toContain('Awaiting first artifact')
  expect(container.textContent).toContain('Workload and routes await the first successful build.')
  await click('Configuration')
  expect(textInput('library/nginx')).toBeUndefined()
})

it('allows manual Build for a Git-owned service awaiting an artifact while configuration writes remain disabled', async () => {
  created = true
  const gitOps = {
    sourceId: 'gitops-source',
    sourceUrl: 'https://github.com/team/configuration',
    blocker: null,
    pendingRemoval: false,
    configurationApplied: true,
  }
  records.set(service.id, {
    ...service,
    gitOps,
    lifecycle: 'AwaitingFirstArtifact',
    spec: {
      ...service.spec,
      image: null,
      imageAuthority: {
        mode: 'PendingSourceBuild',
        configurationId: 'configuration',
        jobId: null,
        generation: null,
        digest: null,
      },
    },
  })
  const base = fetchMock.getMockImplementation()!
  fetchMock.mockImplementation((url, init) => {
    const path = new URL(String(url), 'http://localhost').pathname
    if (path.endsWith('/source-build-configurations'))
      return json([{ ...buildConfiguration, gitOps }])
    if (path.endsWith('/source-repositories')) return json([gitSource])
    return base(url, init)
  })
  await act(async () => render())
  await click('sample')
  await click('Builds')
  expect(button('Build and deploy').disabled).toBe(false)
  expect(button('Remove configuration').disabled).toBe(true)
  await click('Configuration')
  expect(container.textContent).toContain('Edit this configuration in Git.')
})
