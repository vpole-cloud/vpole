// @vitest-environment jsdom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { ConsoleShell } from '../../console/ConsoleShell'
import { createConsoleRegistry } from '../../console/moduleRegistry'
import { webServicesModule } from './WebServicesModule'
import type { IsolationScope, WebService } from '../../types'

const registry = createConsoleRegistry([
  webServicesModule,
  { id: 'test-groups', views: [{ id: 'resource-groups', label: 'Resource groups', component: () => <p>Group provider</p>, access: 'tenant' }] },
])
const group = { id: 'group', name: 'Applications', description: '', tags: [], resourceCount: 1 }
const scope = (id: string, resourceGroupId = group.id, isDefault = false): IsolationScope => ({
  id, resourceGroupId, name: isDefault ? 'default' : 'Production', isDefault, lifecycle: 'active', revision: 1,
  createdAtUtc: '', updatedAtUtc: '',
})
const target = { id: 'target', displayName: 'Local target', workloadConfigurationRevision: 1, hasPublicIngress: true }
const service: WebService = {
  id: 'service', name: 'sample', resourceGroupId: group.id, activeRevision: 1,
  exposure: { mode: 'Private', generatedHostnameLabelPrefix: '' },
  spec: { image: { server: 'docker.io', name: 'library/nginx', tag: 'stable' },
    config: { environmentVariables: {}, portMappings: [{ name: 'http', servicePort: 80, targetPort: '80', protocol: 'TCP' }] },
    probes: { startup: { path: '/', port: '80' }, liveness: { path: '/', port: '80' }, readiness: { path: '/', port: '80' } } },
  deployments: [{ deploymentId: 'deployment', deploymentTargetId: target.id, runtimeCellId: 'cell',
    desiredGeneration: 1, observedGeneration: 0, status: 'PendingApply', resourceRevision: 1,
    fixedReplicas: 1, deploymentRevision: 1, desiredOperation: 'Apply', endpoints: [],
    compute: { displayName: 'Small', profile: { computeProfileId: 'small', computeProfileRevision: 1 },
      requests: { cpuMillis: 100, memoryMi: 128 }, limits: { cpuMillis: 200, memoryMi: 256 }, extendedResources: [] } }],
}
const secondService: WebService = { ...service, id: 'second', name: 'worker' }
let root: Root
let container: HTMLDivElement
let created: boolean
let failCreate: boolean
let failUpdate: boolean
let pendingCreate: Promise<Response> | null
let records: Map<string, WebService>
const selectTenant = vi.fn()
const fetchMock = vi.fn<(url: RequestInfo | URL, init?: RequestInit) => Promise<Response>>()
const json = (body: unknown) => Promise.resolve(new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' } }))
const page = (items: unknown[]) => ({ items, totalCount: items.length, pageNumber: 1, pageSize: 8 })
const storageKey = 'pole.web-services.workspace.v1.user.alpha'

beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  Object.defineProperties(HTMLDialogElement.prototype, {
    showModal: { configurable: true, value: function (this: HTMLDialogElement) { this.open = true } },
    close: { configurable: true, value: function (this: HTMLDialogElement) { this.open = false } },
  })
  vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => { callback(0); return 1 })
  localStorage.clear()
  window.history.replaceState({}, '', '/')
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
  created = false
  failCreate = false
  failUpdate = false
  pendingCreate = null
  records = new Map([[service.id, service], [secondService.id, secondService]])
  selectTenant.mockReset()
  fetchMock.mockReset().mockImplementation((url, init) => {
    const path = new URL(String(url), 'http://localhost').pathname
    if (path.endsWith('/web-services') && init?.method === 'POST') {
      if (pendingCreate) return pendingCreate
      if (failCreate) return Promise.resolve(new Response('This name already exists.', { status: 409 }))
      created = true
      return json({ webServiceId: service.id, deployments: service.deployments })
    }
    if (path.endsWith('/web-services')) return json(page(created ? [...records.values()] : []))
    const match = path.match(/\/web-services\/([^/]+)$/)
    if (match) {
      const resource = records.get(match[1])
      if (!resource) return Promise.resolve(new Response('This service was removed or is unavailable.', { status: 404 }))
      if (init?.method === 'PUT') {
        if (failUpdate) return Promise.resolve(new Response('Configuration changed. Refresh and retry.', { status: 409 }))
        const request = JSON.parse(init.body as string)
        const updated = { ...resource, activeRevision: resource.activeRevision + 1, spec: request.spec }
        records.set(resource.id, updated)
        return json(updated)
      }
      return json(resource)
    }
    if (path.endsWith('/resource-groups')) return json(page([group]))
    if (path.endsWith('/deployment-targets/available')) return json([target])
    if (path.endsWith('/compute-profiles/available')) return json([])
    if (path.endsWith('/releases')) return json({ activeRevision: 1, releases: page([]) })
    if (path.endsWith('/source-build-jobs')) return json(page([]))
    return json([])
  })
  vi.stubGlobal('fetch', fetchMock)
})
afterEach(async () => {
  await act(async () => { root.unmount() })
  container.remove()
  vi.unstubAllGlobals()
})

function render(tenantId = 'alpha', role: 'Owner' | 'Viewer' = 'Owner', authenticated = true) {
  const tenant = { id: tenantId, name: tenantId, role, isDefault: true }
  root.render(<ConsoleShell registry={registry} initialViewId="web-services"
    api={{ baseUrl: 'http://localhost', token: 'test-token' }}
    auth={{ initialized: true, authenticated, userId: 'user', isPlatformAdmin: false, login: () => {}, logout: () => {} }}
    tenants={{ items: [tenant, { ...tenant, id: 'beta', name: 'beta' }], activeTenant: tenant, activeTenantId: tenantId,
      status: 'success', error: '', selectTenant }} />)
}
const visible = (element: Element) => !element.closest('[hidden]')
const button = (label: string) => [...(container.querySelector('dialog') ?? container).querySelectorAll<HTMLButtonElement>('button')]
  .find((node) => visible(node) && (node.textContent === label || node.getAttribute('aria-label') === label))!
const click = async (label: string) => { await act(async () => { button(label).click() }) }
const workspaceTab = (name: string) => [...container.querySelectorAll<HTMLButtonElement>('#web-services-workspace > [role="tablist"] [role="tab"]')]
  .find((node) => node.title === name)!
async function selectTab(name: string) { await act(async () => { workspaceTab(name).click() }) }
const textInput = (placeholder: string) => [...container.querySelectorAll<HTMLInputElement | HTMLTextAreaElement>('input, textarea')]
  .find((node) => visible(node) && node.placeholder === placeholder)!
async function fill(input: HTMLInputElement | HTMLTextAreaElement, value: string) {
  await act(async () => {
    Object.getOwnPropertyDescriptor(input instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype, 'value')!.set!.call(input, value)
    input.dispatchEvent(new Event('input', { bubbles: true }))
  })
}
async function fillCreateForm() {
  await click('New service')
  for (const [placeholder, value] of [['pole-api', 'sample'], ['docker.io', 'docker.io'], ['library/nginx', 'library/nginx']]) {
    await fill(textInput(placeholder), value)
  }
  const selector = [...container.querySelectorAll('select')].find((select) => visible(select) && select.querySelector(`option[value="${group.id}"]`))!
  await act(async () => { selector.value = group.id; selector.dispatchEvent(new Event('change', { bubbles: true })) })
}
async function submit() {
  await act(async () => { [...container.querySelectorAll('form')].find(visible)!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })) })
}

it('lists active isolation scopes by name and sends the chosen identity with tenant headers', async () => {
  const base = fetchMock.getMockImplementation()!
  fetchMock.mockImplementation((url, init) => String(url).endsWith('/isolation-scopes') ? json([
    scope('default-id', group.id, true), scope('production-id'),
    { ...scope('deleting-id'), name: 'Deleting', lifecycle: 'deleting' }, scope('foreign-id', 'another-group'),
  ]) : base(url, init))
  await act(async () => { render() })
  await fillCreateForm()
  const selector = container.querySelector<HTMLSelectElement>('#new-web-service-isolation-scope')!
  expect([...selector.options].map((option) => option.textContent)).toEqual(['Group default', 'Production'])
  expect(selector.value).toBe('')
  expect(container.textContent).not.toContain('MongoDB ObjectId')
  const scopeRequest = fetchMock.mock.calls.find(([url]) => String(url).endsWith('/resource-groups/group/isolation-scopes'))!
  expect(scopeRequest[1]!.headers).toMatchObject({ Authorization: 'Bearer test-token', 'X-Pole-Tenant-ID': 'alpha' })
  const targetRequests = () => fetchMock.mock.calls.filter(([url]) => String(url).includes('/deployment-targets/available'))
  expect(new URL(String(targetRequests().at(-1)![0])).searchParams.get('isolationScopeId')).toBe('default-id')
  await act(async () => { selector.value = 'production-id'; selector.dispatchEvent(new Event('change', { bubbles: true })) })
  expect(new URL(String(targetRequests().at(-1)![0])).searchParams.get('isolationScopeId')).toBe('production-id')
  await submit()
  const [, request] = fetchMock.mock.calls.find(([, init]) => init?.method === 'POST')!
  expect(JSON.parse(request!.body as string).placement.isolationScopeId).toBe('production-id')
})

it('resets scope selection on a group change and ignores late choices from the previous group', async () => {
  const base = fetchMock.getMockImplementation()!
  let finishOther!: (response: Response) => void
  const pendingOther = new Promise<Response>((resolve) => { finishOther = resolve })
  fetchMock.mockImplementation((url, init) => {
    const path = new URL(String(url)).pathname
    if (path.endsWith('/resource-groups')) return json(page([group, { ...group, id: 'other', name: 'Other group' }]))
    if (path.endsWith('/other/isolation-scopes')) return pendingOther
    if (path.endsWith('/group/isolation-scopes')) return json([scope('default-id', group.id, true), scope('production-id')])
    return base(url, init)
  })
  await act(async () => { render() })
  await fillCreateForm()
  const selector = container.querySelector<HTMLSelectElement>('#new-web-service-isolation-scope')!
  const groupSelector = [...container.querySelectorAll('select')].find((select) => select.querySelector('option[value="other"]'))!
  await act(async () => { selector.value = 'production-id'; selector.dispatchEvent(new Event('change', { bubbles: true })) })
  await act(async () => { groupSelector.value = 'other'; groupSelector.dispatchEvent(new Event('change', { bubbles: true })) })
  expect(selector.value).toBe('')
  expect(selector.disabled).toBe(true)
  expect(button('Create service').disabled).toBe(true)
  await act(async () => { groupSelector.value = group.id; groupSelector.dispatchEvent(new Event('change', { bubbles: true })) })
  await act(async () => { finishOther(new Response(JSON.stringify([scope('other-id', 'other')]), { headers: { 'Content-Type': 'application/json' } })) })
  expect([...selector.options].map((option) => option.value)).toEqual(['', 'production-id'])
  expect(selector.value).toBe('')
  const request = fetchMock.mock.calls.find(([url]) => String(url).endsWith('/other/isolation-scopes'))!
  expect(request[1]!.signal!.aborted).toBe(true)
})

it('keeps group default usable when named scopes fail to load and lets the user retry', async () => {
  const base = fetchMock.getMockImplementation()!
  let failScopes = true
  fetchMock.mockImplementation((url, init) => String(url).endsWith('/isolation-scopes')
    ? failScopes ? Promise.resolve(new Response('Scope list unavailable.', { status: 503 })) : json([scope('production-id')])
    : base(url, init))
  await act(async () => { render() })
  await fillCreateForm()
  expect(container.querySelector('[role="alert"]')?.textContent).toContain('You can use the group default.')
  expect(button('Create service').disabled).toBe(false)
  failScopes = false
  await click('Retry scopes')
  expect(container.querySelector('[role="alert"]')).toBeNull()
  expect(container.querySelector('#new-web-service-isolation-scope')!.textContent).toContain('Production')
})
const visibleText = () => {
  const walker = document.createTreeWalker(container, NodeFilter.SHOW_TEXT)
  const text: string[] = []
  while (walker.nextNode()) {
    if (walker.currentNode.parentElement && visible(walker.currentNode.parentElement)) text.push(walker.currentNode.textContent ?? '')
  }
  return text.join(' ')
}

async function traverseHistory(delta: number, events = 1) {
  await act(async () => {
    await new Promise<void>((resolve) => {
      const listener = () => { if (--events === 0) { window.removeEventListener('popstate', listener); resolve() } }
      window.addEventListener('popstate', listener)
      window.history.go(delta)
    })
  })
}

it('opens an exact tenant/resource/section URL before storage, deduplicates tabs and reloads without draft values', async () => {
  localStorage.setItem('pole.web-services.workspace.v1.user.beta', JSON.stringify({ tabs: [{ id: service.id, section: 'builds' }], activeId: service.id }))
  window.history.replaceState({}, '', '/?view=web-services&tenant=beta&resource=second&section=configuration')
  await act(async () => { render() })
  expect(textInput('ASPNETCORE_ENVIRONMENT=Production\nLOG_LEVEL=Information').value).toBe('')
  expect(workspaceTab('worker').getAttribute('aria-selected')).toBe('true')
  const [, request] = fetchMock.mock.calls.find(([url]) => String(url).endsWith('/web-services/second'))!
  expect(request!.headers).toMatchObject({ 'X-Pole-Tenant-ID': 'beta' })
  await fill(textInput('ASPNETCORE_ENVIRONMENT=Production\nLOG_LEVEL=Information'), 'URL_DRAFT=private')
  await click('Deployments')
  expect(new URLSearchParams(window.location.search).get('section')).toBe('deployments')
  await traverseHistory(-1)
  expect(textInput('ASPNETCORE_ENVIRONMENT=Production\nLOG_LEVEL=Information').value).toBe('URL_DRAFT=private')
  await traverseHistory(1)
  expect(visibleText()).toContain('Physical deployments')
  expect(container.querySelectorAll('#web-services-workspace > [role="tablist"] [role="tab"]')).toHaveLength(3)
  expect(localStorage.getItem('pole.web-services.workspace.v1.user.beta')).not.toContain('URL_DRAFT')
  await act(async () => { root.unmount(); root = createRoot(container); render() })
  expect(visibleText()).toContain('Physical deployments')
  await click('Configuration')
  expect(textInput('ASPNETCORE_ENVIRONMENT=Production\nLOG_LEVEL=Information').value).toBe('')
})

it('keeps Back/Forward entries when cancelling departure from a dirty view', async () => {
  created = true
  await act(async () => { render() })
  await click('Resource groups')
  await click('Web services')
  await click('sample')
  await click('Configuration')
  // Reload keeps the history index, so cancellation traverses back to the right entry.
  await act(async () => { root.unmount(); root = createRoot(container); render() })
  await fill(textInput('ASPNETCORE_ENVIRONMENT=Production\nLOG_LEVEL=Information'), 'KEEP=true')
  const currentUrl = window.location.href
  const length = window.history.length
  const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
  await traverseHistory(-3, 2)
  expect(window.location.href).toBe(currentUrl)
  expect(window.history.length).toBe(length)
  expect(textInput('ASPNETCORE_ENVIRONMENT=Production\nLOG_LEVEL=Information').value).toBe('KEEP=true')
  confirm.mockReturnValue(true)
  await traverseHistory(-3)
  expect(visibleText()).toContain('Group provider')
  await traverseHistory(1)
  expect(visibleText()).toContain('All web services')
  confirm.mockRestore()
})

it.each(['missing-tenant', ''])('does not issue resource reads for an unavailable URL tenant %s', async (tenant) => {
  window.history.replaceState({}, '', `/?view=web-services&tenant=${tenant}&resource=service`)
  await act(async () => { render() })
  expect(visibleText()).toContain('This tenant is unavailable to your account.')
  expect(fetchMock).not.toHaveBeenCalled()
  expect(selectTenant).not.toHaveBeenCalled()
  const selector = container.querySelector<HTMLSelectElement>('.tenant-selector select')!
  expect(selector.value).toBe('')
  await act(async () => { selector.value = 'alpha'; selector.dispatchEvent(new Event('change', { bubbles: true })) })
  expect(visibleText()).toContain('All web services')
  expect(new URLSearchParams(window.location.search).get('tenant')).toBe('alpha')
})

it('paginates and searches across the server inventory, restoring the page through history', async () => {
  const base = fetchMock.getMockImplementation()!
  fetchMock.mockImplementation((url, init) => {
    const request = new URL(String(url))
    if (request.pathname.endsWith('/web-services') && !init?.method) {
      const number = Number(request.searchParams.get('pageNumber'))
      const size = Number(request.searchParams.get('pageSize'))
      const search = request.searchParams.get('search') ?? ''
      const all = [...records.values()].filter((item) => item.name.includes(search))
      return json({ items: all.slice((number - 1) * size, number * size), totalCount: all.length, pageNumber: number, pageSize: size })
    }
    return base(url, init)
  })
  window.history.replaceState({}, '', '/?view=web-services&tenant=alpha&resource=catalog&pageSize=1')
  await act(async () => { render() })
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
  const [url, init] = fetchMock.mock.calls.filter(([url]) => new URL(String(url)).pathname.endsWith('/web-services')).at(-1)!
  expect(new URL(String(url)).searchParams.get('search')).toBe('sample')
  expect(init!.headers).toMatchObject({ Authorization: 'Bearer test-token', 'X-Pole-Tenant-ID': 'alpha' })
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
  const pending = new Promise<Response>((resolve) => { finish = resolve })
  let fail = false
  fetchMock.mockImplementation((url, init) => {
    const request = new URL(String(url))
    if (request.pathname.endsWith('/web-services') && !init?.method) {
      if (fail) return Promise.resolve(new Response('Inventory temporarily unavailable.', { status: 503 }))
      if (request.searchParams.get('pageNumber') === '2') return pending
      return json({ items: [service], totalCount: 2, pageNumber: 1, pageSize: 1 })
    }
    return base(url, init)
  })
  window.history.replaceState({}, '', '/?view=web-services&tenant=alpha&resource=catalog&pageSize=1')
  await act(async () => { render() })
  await click('Next page')
  const [, request] = fetchMock.mock.calls.find(([url]) => String(url).includes('pageNumber=2'))!
  await click('Previous page')
  expect(request!.signal!.aborted).toBe(true)
  await act(async () => { finish(new Response(JSON.stringify({ items: [secondService], totalCount: 2, pageNumber: 2, pageSize: 1 }))) })
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
  await act(async () => { render() })
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
  const pending = new Promise<Response>((resolve) => { finish = resolve })
  fetchMock.mockImplementation((url, init) => {
    if (new URL(String(url)).pathname.endsWith('/web-services') && !init?.method) {
      return (init!.headers as Record<string, string>)['X-Pole-Tenant-ID'] === 'alpha'
        ? pending : json(page([secondService]))
    }
    return base(url, init)
  })
  await act(async () => { render() })
  const [, oldRequest] = fetchMock.mock.calls.find(([url]) => new URL(String(url)).pathname.endsWith('/web-services'))!
  const selector = container.querySelector<HTMLSelectElement>('.tenant-selector select')!
  await act(async () => { selector.value = 'beta'; selector.dispatchEvent(new Event('change', { bubbles: true })) })
  expect(oldRequest!.signal!.aborted).toBe(true)
  expect(visibleText()).toContain('worker')
  await act(async () => { finish(new Response(JSON.stringify(page([service])))) })
  expect(visibleText()).not.toContain('sample')
  expect(new URLSearchParams(window.location.search).get('tenant')).toBe('beta')
})

it('retains mounted drafts while reporting an invalid history link', async () => {
  created = true
  await act(async () => { render() })
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
  await act(async () => { render('alpha', 'Viewer') })
  expect(visibleText()).toContain('Tenant viewer access is read-only.')
  expect(container.querySelector('form:not(.inventory-search)')).toBeNull()
})

it('keeps tabs and drafts opened while a creation request is completing', async () => {
  created = true
  let finish!: (response: Response) => void
  pendingCreate = new Promise<Response>((resolve) => { finish = resolve })
  await act(async () => { render() })
  await fillCreateForm()
  await submit()
  await selectTab('All services')
  await click('worker')
  await click('Configuration')
  await fill(textInput('latest'), 'opened-during-create')
  await act(async () => { finish(new Response(JSON.stringify({ webServiceId: service.id, deployments: service.deployments }))) })
  expect(visibleText()).toContain('Service overview')
  await selectTab('worker')
  expect(textInput('latest').value).toBe('opened-during-create')
})

it('returns to the previous page when its last service is deleted', async () => {
  const base = fetchMock.getMockImplementation()!
  fetchMock.mockImplementation((url, init) => {
    const request = new URL(String(url))
    if (request.pathname.endsWith(`/web-services/${secondService.id}`) && init?.method === 'DELETE') {
      records.delete(secondService.id)
      return Promise.resolve(new Response(null, { status: 204 }))
    }
    if (request.pathname.endsWith('/web-services') && !init?.method) {
      const pageNumber = Number(request.searchParams.get('pageNumber'))
      return json({ items: [...records.values()].slice(pageNumber - 1, pageNumber), totalCount: records.size, pageNumber, pageSize: 1 })
    }
    return base(url, init)
  })
  window.history.replaceState({}, '', '/?view=web-services&tenant=alpha&resource=catalog&page=2&pageSize=1')
  await act(async () => { render() })
  await click('Delete worker')
  await click('Delete service')
  expect(visibleText()).toContain('Page 1 of 1')
  expect(visibleText()).toContain('sample')
  expect(visibleText()).not.toContain('worker')
  expect(new URLSearchParams(window.location.search).get('page')).toBe('1')
})

it('keeps the catalog separate, then creates an image with auth/tenant headers and opens Overview', async () => {
  await act(async () => { render() })
  expect(container.querySelector('form:not(.inventory-search)')).toBeNull()
  await fillCreateForm()
  await submit()
  const [, request] = fetchMock.mock.calls.find(([, init]) => init?.method === 'POST')!
  expect(request!.headers).toMatchObject({ Authorization: 'Bearer test-token', 'X-Pole-Tenant-ID': 'alpha' })
  expect(JSON.parse(request!.body as string)).toMatchObject({ name: 'sample', resourceGroupId: group.id, exposure: 'Private',
    placement: { targets: [{ deploymentTargetId: target.id, fixedReplicas: 1 }] } })
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
  await act(async () => { render() })
  await fillCreateForm()
  await submit()
  expect(container.querySelector('[role="alert"]')?.textContent).toContain('This name already exists.')
  expect(textInput('pole-api').value).toBe('sample')
  expect(container.textContent).not.toContain('Service configuration accepted.')
})

it('retains separate service drafts while switching resource tabs and sections', async () => {
  created = true
  await act(async () => { render() })
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
  await act(async () => { render() })
  await fillCreateForm()
  await selectTab('All services')
  await selectTab('New service')
  expect(textInput('pole-api').value).toBe('sample')
  await click('Close New service')
  expect(container.querySelector('dialog')?.textContent).toContain('Your unsaved changes will be lost.')
  await click('Cancel')
  expect(textInput('pole-api').value).toBe('sample')
  await click('Close New service')
  await click('Discard and close')
  expect(container.querySelector('form:not(.inventory-search)')).toBeNull()
  expect(workspaceTab('All services').getAttribute('aria-selected')).toBe('true')
})

it('guards module navigation, tenant selection and browser unload without persisting form values', async () => {
  await act(async () => { render() })
  await fillCreateForm()
  await click('Resource groups')
  expect(container.querySelector('dialog')?.textContent).toContain('Discard unsaved changes?')
  await click('Cancel')
  const selector = container.querySelector<HTMLSelectElement>('.top-band select')!
  await act(async () => { selector.value = 'beta'; selector.dispatchEvent(new Event('change', { bubbles: true })) })
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
  await act(async () => { render() })
  await click('sample')
  await click('Configuration')
  await fill(textInput('latest'), 'saved-tag')
  await submit()
  const [, request] = fetchMock.mock.calls.find(([, init]) => init?.method === 'PUT')!
  expect(request!.headers).toMatchObject({ Authorization: 'Bearer test-token', 'X-Pole-Tenant-ID': 'alpha' })
  expect(workspaceTab('sample').getAttribute('aria-label')).toBe('sample')
  const unload = new Event('beforeunload', { cancelable: true })
  window.dispatchEvent(unload)
  expect(unload.defaultPrevented).toBe(false)
  expect(JSON.parse(localStorage.getItem(storageKey)!)).toEqual({ tabs: [{ id: 'service', section: 'configuration' }], activeId: 'service' })
  await act(async () => { root.unmount() })
  root = createRoot(container)
  await act(async () => { render() })
  expect(textInput('latest').value).toBe('saved-tag')
  expect(workspaceTab('sample').getAttribute('aria-selected')).toBe('true')
})

it('drops a pending navigation confirmation when an external session change removes its draft', async () => {
  await act(async () => { render() })
  await fillCreateForm()
  await click('Resource groups')
  expect(container.querySelector('dialog')).not.toBeNull()
  await act(async () => { render('alpha', 'Owner', false) })
  await act(async () => { render() })
  expect(container.querySelector('dialog')).toBeNull()
  await click('New service')
  await fill(textInput('pole-api'), 'fresh-draft')
  expect(container.querySelector('dialog')).toBeNull()
})

it('keeps an update draft dirty when the server rejects it', async () => {
  created = true
  failUpdate = true
  await act(async () => { render() })
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
  await act(async () => { render() })
  await click('sample')
  await click('Configuration')
  records.set(service.id, { ...service, activeRevision: 2, spec: { ...service.spec, image: { ...service.spec.image, tag: 'server-two' } } })
  await click('Refresh service details')
  expect(textInput('latest').value).toBe('server-two')
  expect(workspaceTab('sample').getAttribute('aria-label')).toBe('sample')
  await fill(textInput('latest'), 'keep-my-draft')
  records.set(service.id, { ...service, activeRevision: 3, spec: { ...service.spec, image: { ...service.spec.image, tag: 'server-three' } } })
  await click('Refresh service details')
  expect(textInput('latest').value).toBe('keep-my-draft')
  expect(workspaceTab('sample').getAttribute('aria-label')).toContain('unsaved changes')
  await click('Discard changes')
  await click('Discard changes')
  expect(textInput('latest').value).toBe('server-three')
  expect(workspaceTab('sample').getAttribute('aria-label')).toBe('sample')
  expect(document.activeElement?.getAttribute('id')).toBe('web-service-service-tab-configuration')
})

it('loads restored resource references directly and reports missing resources without opening a form', async () => {
  localStorage.setItem(storageKey, JSON.stringify({ tabs: [{ id: 'missing', section: 'configuration' }], activeId: 'missing' }))
  await act(async () => { render() })
  expect(visibleText()).toContain('Unable to open this service.')
  expect(container.querySelector('form:not(.inventory-search)')).toBeNull()
  const [, request] = fetchMock.mock.calls.find(([url]) => String(url).endsWith('/web-services/missing'))!
  expect(request!.headers).toMatchObject({ Authorization: 'Bearer test-token', 'X-Pole-Tenant-ID': 'alpha' })
})

it('restores a service outside the catalog page and stores no configuration draft', async () => {
  localStorage.setItem(storageKey, JSON.stringify({ tabs: [{ id: 'service', section: 'configuration' }], activeId: 'service' }))
  await act(async () => { render() })
  expect(textInput('latest').value).toBe('stable')
  expect(workspaceTab('sample').getAttribute('aria-selected')).toBe('true')
  await fill(textInput('ASPNETCORE_ENVIRONMENT=Production\nLOG_LEVEL=Information'), 'DRAFT_ONLY=keep-in-memory')
  expect(localStorage.getItem(storageKey)).not.toContain('DRAFT_ONLY')
  expect(localStorage.getItem(storageKey)).not.toContain('keep-in-memory')
  await act(async () => { render('beta') })
  expect(workspaceTab('sample')).toBeUndefined()
  expect(JSON.parse(localStorage.getItem(storageKey)!)).toEqual({ tabs: [{ id: 'service', section: 'configuration' }], activeId: 'service' })
})

it('allows a viewer to inspect each section without showing mutation forms', async () => {
  created = true
  await act(async () => { render('alpha', 'Viewer') })
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
  pendingCreate = new Promise<Response>((resolve) => { resolveCreate = resolve })
  await act(async () => { render() })
  await fillCreateForm()
  await submit()
  await act(async () => { render('beta') })
  await act(async () => { resolveCreate(new Response(JSON.stringify({ webServiceId: service.id }))) })
  expect(container.textContent).not.toContain('Service configuration accepted.')
  expect(workspaceTab('sample')).toBeUndefined()
  expect(container.querySelector('form:not(.inventory-search)')).toBeNull()
})

it('does not reopen a creation tab when a mutation completes after the draft was closed', async () => {
  let resolveCreate!: (response: Response) => void
  pendingCreate = new Promise<Response>((resolve) => { resolveCreate = resolve })
  await act(async () => { render() })
  await fillCreateForm()
  await submit()
  await click('Close New service')
  await click('Discard and close')
  await act(async () => { resolveCreate(new Response(JSON.stringify({ webServiceId: service.id }))) })
  expect(workspaceTab('sample')).toBeUndefined()
  expect(workspaceTab('All services').getAttribute('aria-selected')).toBe('true')
})

it('moves tab focus with arrow keys and uses Delete to request dirty-tab closure', async () => {
  created = true
  await act(async () => { render() })
  await click('sample')
  await click('Configuration')
  await fill(textInput('latest'), 'keyboard-draft')
  workspaceTab('sample').focus()
  await act(async () => { workspaceTab('sample').dispatchEvent(new KeyboardEvent('keydown', { key: 'Home', bubbles: true })) })
  expect(document.activeElement).toBe(workspaceTab('All services'))
  await act(async () => { workspaceTab('All services').dispatchEvent(new KeyboardEvent('keydown', { key: 'End', bubbles: true })) })
  expect(document.activeElement).toBe(workspaceTab('sample'))
  await act(async () => { workspaceTab('sample').dispatchEvent(new KeyboardEvent('keydown', { key: 'Delete', bubbles: true })) })
  expect(container.querySelector('dialog')?.textContent).toContain('Discard changes to sample?')
})
