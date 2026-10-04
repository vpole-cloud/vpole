// @vitest-environment jsdom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { AppShell } from '../../shell/AppShell'
import { createAppRegistry } from '../../shell/moduleRegistry'
import type { HostnameBinding, TenantDomainZone, WebService } from '../../types'
import { WebServiceDomains } from '../web-services/WebServiceDomains'
import { DomainsView } from './DomainsView'

const zone: TenantDomainZone = { id: 'zone', tenantId: 'alpha', deploymentTargetId: 'target', label: 'company', domain: 'company.apps.test', kind: 'managed-reservation', revision: 1, createdAtUtc: '2026-01-01T00:00:00Z', updatedAtUtc: '2026-01-02T00:00:00Z' }
const generated: HostnameBinding = { id: 'generated', tenantId: 'alpha', tenantDomainZoneId: 'generated-zone', deploymentTargetId: 'target', resourceType: 'pole/web-service', resourceId: 'app', resourceDeploymentId: 'dep', relativeName: 'app', hostname: 'app.generated.apps.test', kind: 'generated', createdAtUtc: zone.createdAtUtc }
const custom: HostnameBinding = { ...generated, id: 'old', tenantDomainZoneId: zone.id, relativeName: 'api', hostname: 'api.company.apps.test', kind: 'custom' }
let service: WebService; let rows: HostnameBinding[]; let targetAvailable: boolean; let detachFails: boolean; let createFails: boolean; let attachFails: boolean
let root: Root; let container: HTMLDivElement
const fetchMock = vi.fn<(input: RequestInfo | URL, init?: RequestInit) => Promise<Response>>()
const json = (value: unknown) => Promise.resolve(new Response(JSON.stringify(value)))
function DomainService() { return <WebServiceDomains service={service} active onDirtyChange={() => {}} onChanged={async () => {}} /> }
const registry = createAppRegistry([
  { id: 'domains', views: [{ id: 'domains', label: 'Domains', component: DomainsView, access: 'tenant' }] },
  { id: 'web', views: [{ id: 'web-services', label: 'Web services', component: DomainService, access: 'tenant' }], resourceTypes: [{ id: 'pole/web-service', label: 'Web service', viewId: 'web-services' }] },
  { id: 'groups', views: [{ id: 'resource-groups', label: 'Groups', component: () => null, access: 'tenant' }], resourceTypes: [{ id: 'pole/resource-group', label: 'Group', viewId: 'resource-groups' }] },
])
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  Object.defineProperties(HTMLDialogElement.prototype, { showModal: { configurable: true, value: function (this: HTMLDialogElement) { this.open = true } }, close: { configurable: true, value: function (this: HTMLDialogElement) { this.open = false } } })
  vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => { callback(0); return 1 })
  localStorage.clear(); window.history.replaceState({}, '', '/?view=domains&resource=catalog')
  container = document.createElement('div'); document.body.append(container); root = createRoot(container)
  service = { id: 'app', name: 'Application', isolationScopeId: 'scope', exposure: { mode: 'Public' }, deployments: [{ deploymentId: 'dep', deploymentTargetId: 'target', status: 'Applied', desiredOperation: 'Apply', desiredGeneration: 2, observedGeneration: 2, deploymentRevision: 2 }, { deploymentId: 'failed', deploymentTargetId: 'other', status: 'Failed', failureCode: 'IngressApplyFailed', desiredOperation: 'Apply', desiredGeneration: 2, observedGeneration: 1, deploymentRevision: 2 }] } as WebService
  rows = [generated, custom]; targetAvailable = true; detachFails = false; createFails = false; attachFails = false
  fetchMock.mockReset().mockImplementation((input, init) => {
    const url = new URL(String(input), 'http://localhost'); const tenantId = new Headers(init?.headers).get('X-Pole-Tenant-ID')
    if (tenantId === 'beta') return json([])
    if (url.pathname.endsWith('/deployment-targets/available')) return json(targetAvailable ? [{ id: 'target', displayName: 'Local cluster', hasPublicIngress: true, publicBaseDomain: 'apps.test' }] : [])
    if (url.pathname.endsWith('/domain-zones')) {
      if (init?.method === 'POST') return createFails ? Promise.resolve(new Response(JSON.stringify({ code: 'domains.zone.already-reserved', detail: 'foreign-private-canary' }), { status: 409 })) : json(zone)
      return json([zone])
    }
    if (url.pathname.endsWith('/domain-zones/zone')) return json(zone)
    if (url.pathname.endsWith('/domain-zones/zone/bindings')) return json({ items: [{ binding: custom, resourceDisplayName: 'Application', resourceGroupId: 'group', placement: service.deployments[1] }], totalCount: 26, pageNumber: Number(url.searchParams.get('pageNumber')), pageSize: 25 })
    if (url.pathname.endsWith('/resource-groups/group')) return json({ id: 'group', name: 'Production' })
    if (url.pathname.endsWith('/deployments/failed/hostnames')) return json([])
    if (url.pathname.endsWith('/deployments/dep/hostnames')) {
      if (init?.method === 'POST' && attachFails) return Promise.resolve(new Response(JSON.stringify({ code: 'domains.hostname.already-bound', detail: 'foreign-private-canary', resourceId: 'inaccessible-owner' }), { status: 409 }))
      if (init?.method === 'POST') { const body = JSON.parse(init.body as string); rows.push({ ...custom, id: 'new', relativeName: body.relativeName, hostname: `${body.relativeName}.${zone.domain}` }); return json(service.deployments[0]) }
      return json([...rows])
    }
    if (url.pathname.endsWith('/hostnames/old')) {
      if (detachFails) return Promise.resolve(new Response(JSON.stringify({ code: 'domains.hostname.delete.conflict', detail: 'foreign-private-canary' }), { status: 409 }))
      rows = rows.filter((row) => row.id !== 'old'); return json(service.deployments[0])
    }
    return json([])
  })
  vi.stubGlobal('fetch', fetchMock)
})
afterEach(async () => { await act(async () => root.unmount()); container.remove(); vi.unstubAllGlobals() })
async function render(role: 'Owner' | 'Viewer' = 'Owner', tenantId = 'alpha', view = 'domains') {
  const tenant = { id: tenantId, name: tenantId, role, isDefault: true }
  await act(async () => root.render(<AppShell registry={registry} initialViewId={view} api={{ baseUrl: 'http://localhost', token: 'token' }} auth={{ initialized: true, authenticated: true, userId: 'user', isPlatformAdmin: false, login: () => {}, logout: () => {} }} tenants={{ items: [tenant], activeTenant: tenant, activeTenantId: tenantId, selectTenant: () => {}, status: 'success', error: '' }} />))
}
const visible = (element: Element) => !element.closest('[hidden]')
const text = () => [...container.querySelectorAll('#app-content *')].filter(visible).map((item) => item.textContent).join(' ')
async function click(name: string) { await act(async () => {
  const scope = container.querySelector('dialog[open]') ?? container
  const element = [...scope.querySelectorAll<HTMLElement>('button,a')].find((item) => visible(item) && (item.textContent === name || item.getAttribute('aria-label') === name))
  if (!element) throw new Error(`Missing action ${name}`); element.click()
}) }
function field(name: string) { return [...container.querySelectorAll('label')].find((label) => visible(label) && label.querySelector('.field__label')?.textContent === name)!.querySelector<HTMLInputElement | HTMLSelectElement>('input,select')! }
async function fill(name: string, value: string) { await act(async () => { const input = field(name); Object.getOwnPropertyDescriptor(input instanceof HTMLSelectElement ? HTMLSelectElement.prototype : HTMLInputElement.prototype, 'value')!.set!.call(input, value); input.dispatchEvent(new Event(input instanceof HTMLSelectElement ? 'change' : 'input', { bubbles: true })) }) }

it('separates create and ownership, links resource/group and shows target failure with bounded pages', async () => {
  await render(); expect(container.querySelector('form')).toBeNull(); await click(zone.domain)
  expect(text()).toContain('Production'); expect(text()).toContain('IngressApplyFailed'); expect(text()).toContain('Generation 1/2')
  const owner = [...container.querySelectorAll<HTMLAnchorElement>('a')].find((item) => item.textContent === 'Application')!
  expect(owner.href).toContain('section=domains')
  expect([...container.querySelectorAll<HTMLButtonElement>('button')].find((item) => item.textContent === 'Remove reservation')!.disabled).toBe(true)
  await click('Next owners'); expect(fetchMock.mock.calls.some(([url]) => String(url).includes('pageNumber=2&pageSize=25'))).toBe(true)
})

it('retains a dirty reservation across workspace tabs and exposes safe conflict guidance', async () => {
  await render(); await click('Reserve zone'); await fill('Deployment target', 'target'); await fill('Zone label', 'company'); await click('All zones'); await click('Reserve zone')
  expect(field('Zone label').value).toBe('company'); createFails = true
  await act(async () => container.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })))
  expect(text()).toContain('already reserved'); expect(text()).not.toContain('foreign-private-canary')
  expect(JSON.stringify(localStorage)).not.toContain('company')
})

it('preserves partial alias replacement and retries detach without duplicating the saved replacement', async () => {
  window.history.replaceState({}, '', '/?view=web-services'); await render('Owner', 'alpha', 'web-services')
  expect(text()).toContain('IngressApplyFailed'); expect(text()).toContain('Generated · maintained'); await click('Change alias'); await fill('Hostname label', 'next'); detachFails = true
  await act(async () => container.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })))
  expect(text()).toContain('Detach could not be confirmed'); expect(text()).toContain('next.company.apps.test'); expect(text()).toContain('api.company.apps.test'); expect(text()).not.toContain('foreign-private-canary')
  detachFails = false
  await act(async () => container.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })))
  expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST')).toHaveLength(1)
  expect(rows.map((row) => row.id)).toEqual(['generated', 'new'])
  expect(field('Hostname label').value).toBe('')
})

it('requires detach confirmation and keeps generated ownership protected', async () => {
  window.history.replaceState({}, '', '/?view=web-services'); await render('Owner', 'alpha', 'web-services'); await click('Detach alias')
  expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'DELETE')).toBe(false)
  await click('Cancel'); await click('Detach alias'); await click('Detach alias')
  const request = fetchMock.mock.calls.find(([, init]) => init?.method === 'DELETE')!
  expect(String(request[0])).toContain('/deployments/dep/hostnames/old'); expect(request[1]?.signal).toBeInstanceOf(AbortSignal)
  expect(rows.map((row) => row.id)).toEqual(['generated'])
})

it('explains a routing conflict without revealing the inaccessible owner or losing the hostname draft', async () => {
  window.history.replaceState({}, '', '/?view=web-services'); await render('Owner', 'alpha', 'web-services'); await fill('Reserved zone', 'zone'); await fill('Hostname label', 'conflict'); attachFails = true
  await act(async () => container.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })))
  expect(text()).toContain('already reserved in the configured routing scope'); expect(text()).not.toContain('foreign-private-canary'); expect(text()).not.toContain('inaccessible-owner')
  expect(field('Hostname label').value).toBe('conflict'); expect(rows).toHaveLength(2)
})

it('blocks missing ingress and viewer mutations and clears drafts on tenant switch', async () => {
  window.history.replaceState({}, '', '/?view=web-services'); await render('Owner', 'alpha', 'web-services'); await fill('Reserved zone', 'zone'); await fill('Hostname label', 'draft')
  await render('Viewer', 'alpha', 'web-services'); expect(text()).not.toContain('Attach alias'); expect(text()).not.toContain('Detach alias')
  await render('Owner', 'beta', 'web-services'); expect(container.querySelector<HTMLInputElement>('input')?.value ?? '').toBe('')
  targetAvailable = false; await render('Owner', 'alpha', 'web-services'); expect(text()).toContain('Public ingress is unavailable'); expect(text()).not.toContain('Attach alias')
})
