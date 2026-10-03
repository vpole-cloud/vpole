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
let root: Root
let container: HTMLDivElement
let created: boolean
let failCreate: boolean
let pendingCreate: Promise<Response> | null
const fetchMock = vi.fn<(url: RequestInfo | URL, init?: RequestInit) => Promise<Response>>()
const json = (body: unknown) => Promise.resolve(new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' } }))
const page = (items: unknown[]) => ({ items, totalCount: items.length, pageNumber: 1, pageSize: 8 })

beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  localStorage.clear()
  container = document.createElement('div')
  root = createRoot(container)
  created = false
  failCreate = false
  pendingCreate = null
  fetchMock.mockReset().mockImplementation((url, init) => {
    const path = new URL(String(url), 'http://localhost').pathname
    if (path.endsWith('/web-services') && init?.method === 'POST') {
      if (pendingCreate) return pendingCreate
      if (failCreate) return Promise.resolve(new Response('This name already exists.', { status: 409 }))
      created = true
      return json({ webServiceId: service.id, deployments: service.deployments })
    }
    if (path.endsWith('/web-services')) return json(page(created ? [service] : []))
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
  vi.unstubAllGlobals()
})

function render(tenantId = 'alpha', role: 'Owner' | 'Viewer' = 'Owner') {
  const tenant = { id: tenantId, name: tenantId, role, isDefault: true }
  root.render(<ConsoleShell registry={registry} initialViewId="web-services"
    api={{ baseUrl: 'http://localhost', token: 'test-token' }}
    auth={{ initialized: true, authenticated: true, userId: 'user', isPlatformAdmin: false, login: () => {}, logout: () => {} }}
    tenants={{ items: [tenant], activeTenant: tenant, activeTenantId: tenantId, status: 'success', error: '', selectTenant: () => {} }} />)
}

async function fillCreateForm() {
  for (const [placeholder, value] of [['pole-api', 'sample'], ['docker.io', 'docker.io'], ['library/nginx', 'library/nginx']]) {
    const input = container.querySelector<HTMLInputElement>(`input[placeholder="${placeholder}"]`)!
    await act(async () => {
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(input, value)
      input.dispatchEvent(new Event('input', { bubbles: true }))
    })
  }
  const groupSelector = [...container.querySelectorAll('select')].find((select) => select.querySelector(`option[value="${group.id}"]`))!
  await act(async () => {
    groupSelector.value = group.id
    groupSelector.dispatchEvent(new Event('change', { bubbles: true }))
  })
}

it('creates an image service with tenant/auth headers and opens its reconciliation context', async () => {
  await act(async () => { render() })
  await fillCreateForm()
  await act(async () => { container.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })) })
  const [, request] = fetchMock.mock.calls.find(([, init]) => init?.method === 'POST')!
  expect(request!.headers).toMatchObject({ Authorization: 'Bearer test-token', 'X-Pole-Tenant-ID': 'alpha' })
  expect(JSON.parse(request!.body as string)).toMatchObject({ name: 'sample', resourceGroupId: group.id, exposure: 'Private',
    placement: { targets: [{ deploymentTargetId: target.id, fixedReplicas: 1 }] } })
  expect(container.textContent).toContain('Physical deployments')
  expect(container.textContent).toContain('PendingApply')
  expect(container.textContent).toContain('Service configuration accepted.')
})

it('preserves form input and shows a concrete API failure', async () => {
  failCreate = true
  await act(async () => { render() })
  await fillCreateForm()
  await act(async () => { container.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })) })
  expect(container.querySelector('[role="alert"]')?.textContent).toContain('This name already exists.')
  expect(container.querySelector<HTMLInputElement>('input[placeholder="pole-api"]')?.value).toBe('sample')
  expect(container.textContent).not.toContain('Service configuration accepted.')
})

it('allows a viewer to inspect deployments without showing a mutation form', async () => {
  created = true
  await act(async () => { render('alpha', 'Viewer') })
  expect(container.querySelector('form')).toBeNull()
  const inspect = [...container.querySelectorAll('button')].find((button) => button.textContent === 'Inspect')!
  await act(async () => { inspect.click() })
  expect(container.textContent).toContain('Physical deployments')
  expect([...container.querySelectorAll('button')].filter((button) => ['New service', 'Delete', 'Retry'].includes(button.textContent ?? ''))
    .every((button) => button.disabled)).toBe(true)
})

it('does not announce or select an old-tenant creation after a tenant switch', async () => {
  let resolveCreate!: (response: Response) => void
  pendingCreate = new Promise<Response>((resolve) => { resolveCreate = resolve })
  await act(async () => { render() })
  await fillCreateForm()
  await act(async () => { container.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })) })
  await act(async () => { render('beta') })
  await act(async () => { resolveCreate(new Response(JSON.stringify({ webServiceId: service.id }))) })
  expect(container.textContent).not.toContain('Service configuration accepted.')
  expect(container.textContent).not.toContain('Physical deployments')
  expect(container.querySelector<HTMLInputElement>('input[placeholder="pole-api"]')?.value).toBe('')
})
