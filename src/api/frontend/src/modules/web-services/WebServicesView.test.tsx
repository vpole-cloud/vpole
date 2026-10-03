// @vitest-environment jsdom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { ConsoleShell } from '../../console/ConsoleShell'
import { createConsoleRegistry } from '../../console/moduleRegistry'
import { webServicesModule } from './WebServicesModule'
import type { WebService } from '../../types'

const registry = createConsoleRegistry([
  webServicesModule,
  { id: 'test-groups', views: [{ id: 'resource-groups', label: 'Test groups', component: () => <p>Group provider</p>, access: 'tenant' }] },
])
const group = { id: 'group', name: 'Applications', description: '', tags: [], resourceCount: 1 }
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
const visibleText = () => {
  const walker = document.createTreeWalker(container, NodeFilter.SHOW_TEXT)
  const text: string[] = []
  while (walker.nextNode()) {
    if (walker.currentNode.parentElement && visible(walker.currentNode.parentElement)) text.push(walker.currentNode.textContent ?? '')
  }
  return text.join(' ')
}

it('keeps the catalog separate, then creates an image with auth/tenant headers and opens Overview', async () => {
  await act(async () => { render() })
  expect(container.querySelector('form')).toBeNull()
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
  expect(container.querySelector('form')).toBeNull()
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
  expect(container.querySelector('form')).toBeNull()
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
  expect(container.querySelector('form')).toBeNull()
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
  expect(container.querySelector('form')).toBeNull()
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
