// @vitest-environment jsdom
import { act } from 'react'
import { deferred, strictFetch } from '../../test/http'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { AppShell } from '../../shell/AppShell'
import { createAppRegistry } from '../../shell/moduleRegistry'
import type { HostnameBinding, TenantDomainZone, WebService } from '../../types'
import { WebServiceDomains } from '../web-services/WebServiceDomains'
import { DomainsView } from './DomainsView'
import { SessionQueries } from '../../data/SessionQueries'
import { RealtimeProvider } from '../../realtime/RealtimeProvider'
import { createRealtimeConnection } from '../../realtime/connection'
import { createRealtimeSession } from '../../realtime/sessionApi'
import { FakeConnection } from '../../test/fakeRealtimeConnection'

vi.mock('../../realtime/connection', () => ({ createRealtimeConnection: vi.fn() }))
vi.mock('../../realtime/sessionApi', () => ({
  createRealtimeSession: vi.fn(),
  revokeRealtimeSession: vi.fn(async () => {}),
}))

const zone: TenantDomainZone = {
  id: 'zone',
  tenantId: 'alpha',
  deploymentTargetId: 'target',
  label: 'company',
  domain: 'company.apps.test',
  kind: 'managed-reservation',
  revision: 1,
  createdAtUtc: '2026-01-01T00:00:00Z',
  updatedAtUtc: '2026-01-02T00:00:00Z',
}
const generated: HostnameBinding = {
  id: 'generated',
  tenantId: 'alpha',
  tenantDomainZoneId: 'generated-zone',
  deploymentTargetId: 'target',
  resourceType: 'pole/web-service',
  resourceId: 'app',
  resourceDeploymentId: 'dep',
  relativeName: 'app',
  hostname: 'app.generated.apps.test',
  kind: 'generated',
  createdAtUtc: zone.createdAtUtc,
}
const custom: HostnameBinding = {
  ...generated,
  id: 'old',
  tenantDomainZoneId: zone.id,
  relativeName: 'api',
  hostname: 'api.company.apps.test',
  kind: 'custom',
}
let service: WebService
let rows: HostnameBinding[]
let targetAvailable: boolean
let detachFails: boolean
let createFails: boolean
let attachFails: boolean
let root: Root
let container: HTMLDivElement
let liveConnection: FakeConnection
let heldZones: Promise<Response> | undefined
let heldBindings: Promise<Response> | undefined
let heldAttach: Promise<Response> | undefined
let zoneRows: TenantDomainZone[]
let zonesFail: boolean
let bindingsFail: boolean
let currentZone: TenantDomainZone
let ownerCount: number
let ownersFail: boolean
let zoneFail: boolean
let heldZone: Promise<Response> | undefined
let heldOwners: Promise<Response> | undefined
const requests = strictFetch()
const fetchMock = requests.fetch
const json = (value: unknown) => Promise.resolve(new Response(JSON.stringify(value)))
function DomainService() {
  return (
    <WebServiceDomains
      service={service}
      active
      onDirtyChange={() => {}}
      onChanged={async () => {}}
    />
  )
}
const registry = createAppRegistry([
  {
    id: 'domains',
    views: [{ id: 'domains', label: 'Domains', component: DomainsView, access: 'tenant' }],
  },
  {
    id: 'web',
    views: [
      { id: 'web-services', label: 'Web services', component: DomainService, access: 'tenant' },
    ],
    resourceTypes: [{ id: 'pole/web-service', label: 'Web service', viewId: 'web-services' }],
  },
  {
    id: 'groups',
    views: [{ id: 'resource-groups', label: 'Groups', component: () => null, access: 'tenant' }],
    resourceTypes: [{ id: 'pole/resource-group', label: 'Group', viewId: 'resource-groups' }],
  },
])
beforeEach(() => {
  requests.reset()
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
  window.history.replaceState({}, '', '/?view=domains&resource=catalog')
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
  service = {
    id: 'app',
    name: 'Application',
    isolationScopeId: 'scope',
    exposure: { mode: 'Public' },
    deployments: [
      {
        deploymentId: 'dep',
        deploymentTargetId: 'target',
        status: 'Applied',
        desiredOperation: 'Apply',
        desiredGeneration: 2,
        observedGeneration: 2,
        deploymentRevision: 2,
      },
      {
        deploymentId: 'failed',
        deploymentTargetId: 'other',
        status: 'Failed',
        failureCode: 'IngressApplyFailed',
        desiredOperation: 'Apply',
        desiredGeneration: 2,
        observedGeneration: 1,
        deploymentRevision: 2,
      },
    ],
  } as WebService
  rows = [generated, custom]
  targetAvailable = true
  detachFails = false
  createFails = false
  attachFails = false
  heldZones = undefined
  heldBindings = undefined
  heldAttach = undefined
  zoneRows = [zone]
  zonesFail = false
  bindingsFail = false
  currentZone = { ...zone }
  ownerCount = 26
  ownersFail = false
  zoneFail = false
  heldZone = undefined
  heldOwners = undefined
  vi.mocked(createRealtimeConnection).mockImplementation(() => {
    liveConnection = new FakeConnection()
    return liveConnection
  })
  vi.mocked(createRealtimeSession).mockImplementation(async () => ({
    expiresAtUtc: new Date(Date.now() + 300000).toISOString(),
  }))
  fetchMock.mockReset().mockImplementation((input, init) => {
    const url = new URL(String(input), 'http://localhost')
    const tenantId = new Headers(init?.headers).get('X-Pole-Tenant-ID')
    if (
      tenantId === 'beta' &&
      (url.pathname.endsWith('/domain-zones') ||
        url.pathname.endsWith('/deployment-targets/available') ||
        url.pathname.endsWith('/hostnames'))
    )
      return json([])
    if (url.pathname.endsWith('/deployment-targets/available'))
      return json(
        targetAvailable
          ? [
              {
                id: 'target',
                displayName: 'Local cluster',
                hasPublicIngress: true,
                publicBaseDomain: 'apps.test',
              },
            ]
          : [],
      )
    if (url.pathname.endsWith('/domain-zones')) {
      if (init?.method === 'POST')
        return createFails
          ? Promise.resolve(
              new Response(
                JSON.stringify({
                  code: 'domains.zone.already-reserved',
                  detail: 'foreign-private-canary',
                }),
                { status: 409 },
              ),
            )
          : json(zone)
      return (
        heldZones ??
        (zonesFail ? Promise.resolve(new Response('{}', { status: 503 })) : json(zoneRows))
      )
    }
    if (url.pathname.endsWith('/domain-zones/zone')) {
      if (init?.method === 'DELETE') return Promise.resolve(new Response(null, { status: 204 }))
      return (
        heldZone?.then((response) => response.clone()) ??
        (zoneFail ? Promise.resolve(new Response('{}', { status: 503 })) : json(currentZone))
      )
    }
    if (url.pathname.endsWith('/domain-zones/zone/bindings'))
      return (
        heldOwners?.then((response) => response.clone()) ??
        (ownersFail
          ? Promise.resolve(new Response('{}', { status: 503 }))
          : json(ownerSnapshot(Number(url.searchParams.get('pageNumber')))))
      )
    if (url.pathname.endsWith('/resource-groups/group'))
      return json({ id: 'group', name: 'Production' })
    if (url.pathname.endsWith('/deployments/failed/hostnames')) return json([])
    if (url.pathname.endsWith('/deployments/dep/hostnames')) {
      if (init?.method === 'POST' && attachFails)
        return Promise.resolve(
          new Response(
            JSON.stringify({
              code: 'domains.hostname.already-bound',
              detail: 'foreign-private-canary',
              resourceId: 'inaccessible-owner',
            }),
            { status: 409 },
          ),
        )
      if (init?.method === 'POST') {
        const body = JSON.parse(init.body as string)
        rows.push({
          ...custom,
          id: 'new',
          relativeName: body.relativeName,
          hostname: `${body.relativeName}.${zone.domain}`,
        })
        return heldAttach ?? json(service.deployments[0])
      }
      return (
        heldBindings ??
        (bindingsFail ? Promise.resolve(new Response('{}', { status: 503 })) : json([...rows]))
      )
    }
    if (url.pathname.endsWith('/hostnames/old')) {
      if (detachFails)
        return Promise.resolve(
          new Response(
            JSON.stringify({
              code: 'domains.hostname.delete.conflict',
              detail: 'foreign-private-canary',
            }),
            { status: 409 },
          ),
        )
      rows = rows.filter((row) => row.id !== 'old')
      return json(service.deployments[0])
    }
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
  role: 'Owner' | 'Viewer' = 'Owner',
  tenantId = 'alpha',
  view = 'domains',
  token = 'token',
  live = false,
) {
  const tenant = { id: tenantId, name: tenantId, role, isDefault: true }
  const content = (
    <AppShell
      registry={registry}
      initialViewId={view}
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
        activeTenantId: tenantId,
        selectTenant: () => {},
        status: 'success',
        error: '',
      }}
    />
  )
  await act(async () =>
    root.render(
      live ? (
        <SessionQueries identity="http://localhost:true:user">
          <RealtimeProvider baseUrl="http://localhost" token={token} enabled userId="user">
            {content}
          </RealtimeProvider>
        </SessionQueries>
      ) : (
        content
      ),
    ),
  )
}
const visible = (element: Element) => !element.closest('[hidden]')
const text = () =>
  [...container.querySelectorAll('#app-content *')]
    .filter(visible)
    .map((item) => item.textContent)
    .join(' ')
async function click(name: string) {
  await act(async () => {
    const scope = container.querySelector('dialog[open]') ?? container
    const element = [...scope.querySelectorAll<HTMLElement>('button,a')].find(
      (item) =>
        visible(item) && (item.textContent === name || item.getAttribute('aria-label') === name),
    )
    if (!element) throw new Error(`Missing action ${name}`)
    element.click()
  })
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

it('separates create and ownership, links resource/group and shows target failure with bounded pages', async () => {
  await render()
  expect(container.querySelector('form')).toBeNull()
  await click(zone.domain)
  expect(text()).toContain('Production')
  expect(text()).toContain('IngressApplyFailed')
  expect(text()).toContain('Generation 1/2')
  const owner = [...container.querySelectorAll<HTMLAnchorElement>('a')].find(
    (item) => item.textContent === 'Application',
  )!
  expect(owner.href).toContain('section=domains')
  expect(
    [...container.querySelectorAll<HTMLButtonElement>('button')].find(
      (item) => item.textContent === 'Remove reservation',
    )!.disabled,
  ).toBe(true)
  await click('Next owners')
  expect(
    fetchMock.mock.calls.some(([url]) => String(url).includes('pageNumber=2&pageSize=25')),
  ).toBe(true)
})

it('retains a dirty reservation across workspace tabs and exposes safe conflict guidance', async () => {
  await render()
  await click('Reserve zone')
  await fill('Deployment target', 'target')
  await fill('Zone label', 'company')
  await click('All zones')
  await click('Reserve zone')
  expect(field('Zone label').value).toBe('company')
  createFails = true
  await act(async () =>
    container
      .querySelector('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })),
  )
  expect(text()).toContain('already reserved')
  expect(text()).not.toContain('foreign-private-canary')
  expect(JSON.stringify(localStorage)).not.toContain('company')
})

it('preserves partial alias replacement and retries detach without duplicating the saved replacement', async () => {
  window.history.replaceState({}, '', '/?view=web-services')
  await render('Owner', 'alpha', 'web-services')
  expect(text()).toContain('IngressApplyFailed')
  expect(text()).toContain('Generated · maintained')
  await click('Change alias')
  await fill('Hostname label', 'next')
  detachFails = true
  await act(async () =>
    container
      .querySelector('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })),
  )
  expect(text()).toContain('Detach could not be confirmed')
  expect(text()).toContain('next.company.apps.test')
  expect(text()).toContain('api.company.apps.test')
  expect(text()).not.toContain('foreign-private-canary')
  detachFails = false
  await act(async () =>
    container
      .querySelector('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })),
  )
  expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST')).toHaveLength(1)
  expect(rows.map((row) => row.id)).toEqual(['generated', 'new'])
  expect(field('Hostname label').value).toBe('')
})

it('requires detach confirmation and keeps generated ownership protected', async () => {
  window.history.replaceState({}, '', '/?view=web-services')
  await render('Owner', 'alpha', 'web-services')
  await click('Detach alias')
  expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'DELETE')).toBe(false)
  await click('Cancel')
  await click('Detach alias')
  await click('Detach alias')
  const request = fetchMock.mock.calls.find(([, init]) => init?.method === 'DELETE')!
  expect(String(request[0])).toContain('/deployments/dep/hostnames/old')
  expect(request[1]?.signal).toBeInstanceOf(AbortSignal)
  expect(rows.map((row) => row.id)).toEqual(['generated'])
})

it('explains a routing conflict without revealing the inaccessible owner or losing the hostname draft', async () => {
  window.history.replaceState({}, '', '/?view=web-services')
  await render('Owner', 'alpha', 'web-services')
  await fill('Reserved zone', 'zone')
  await fill('Hostname label', 'conflict')
  attachFails = true
  await act(async () =>
    container
      .querySelector('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })),
  )
  expect(text()).toContain('already reserved in the configured routing scope')
  expect(text()).not.toContain('foreign-private-canary')
  expect(text()).not.toContain('inaccessible-owner')
  expect(field('Hostname label').value).toBe('conflict')
  expect(rows).toHaveLength(2)
})

it('blocks missing ingress and viewer mutations and clears drafts on tenant switch', async () => {
  window.history.replaceState({}, '', '/?view=web-services')
  await render('Owner', 'alpha', 'web-services')
  await fill('Reserved zone', 'zone')
  await fill('Hostname label', 'draft')
  await render('Viewer', 'alpha', 'web-services')
  expect(text()).not.toContain('Attach alias')
  expect(text()).not.toContain('Detach alias')
  await render('Owner', 'beta', 'web-services')
  expect(container.querySelector<HTMLInputElement>('input')?.value ?? '').toBe('')
  targetAvailable = false
  await render('Owner', 'alpha', 'web-services')
  expect(text()).toContain('Public ingress is unavailable')
  expect(text()).not.toContain('Attach alias')
})

it('preserves a reservation draft when credentials refresh and sends the new credential', async () => {
  await render()
  await click('Reserve zone')
  await fill('Deployment target', 'target')
  await fill('Zone label', 'draft')
  await render('Owner', 'alpha', 'domains', 'fresh')
  expect(field('Zone label').value).toBe('draft')
  await act(async () =>
    container
      .querySelector('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })),
  )
  const post = fetchMock.mock.calls.find(
    ([url, init]) => String(url).endsWith('/domain-zones') && init?.method === 'POST',
  )!
  expect(new Headers(post[1]?.headers).get('Authorization')).toBe('Bearer fresh')
})

it('observes conservative binding hints without replacing an alias draft and respects public eligibility denial', async () => {
  vi.useFakeTimers()
  window.history.replaceState({}, '', '/?view=web-services')
  await render('Owner', 'alpha', 'web-services', 'token', true)
  await act(async () => vi.advanceTimersByTimeAsync(500))
  await fill('Reserved zone', 'zone')
  await fill('Hostname label', 'unsaved-alias')
  rows = [
    ...rows,
    {
      ...custom,
      id: 'other-binding',
      relativeName: 'committed',
      hostname: 'committed.company.apps.test',
    },
  ]
  await act(async () =>
    liveConnection.changed({
      version: 1,
      topic: 'domains',
      tenantId: 'alpha',
      resourceId: 'other-binding',
    }),
  )
  await act(async () => vi.advanceTimersByTimeAsync(250))
  expect(text()).toContain('committed.company.apps.test')
  expect(field('Hostname label').value).toBe('unsaved-alias')
  targetAvailable = false
  await act(async () =>
    liveConnection.changed({
      version: 1,
      topic: 'deployment-target-eligibility',
      tenantId: 'alpha',
    }),
  )
  await act(async () => vi.advanceTimersByTimeAsync(250))
  expect(text()).toContain('Public ingress is unavailable')
  expect(text()).not.toContain('Attach alias')
  expect(fetchMock.mock.calls.some(([, init]) => init?.method && init.method !== 'GET')).toBe(false)
})

const aliasPosts = () =>
  fetchMock.mock.calls.filter(
    ([url, init]) => String(url).endsWith('/hostnames') && init?.method === 'POST',
  )
const attachButton = () =>
  [...container.querySelectorAll<HTMLButtonElement>('button')].find(
    (item) => visible(item) && item.textContent === 'Attach alias',
  )!
async function aliasDraft() {
  vi.useFakeTimers()
  window.history.replaceState({}, '', '/?view=web-services')
  await render('Owner', 'alpha', 'web-services', 'token', true)
  await act(async () => vi.advanceTimersByTimeAsync(500))
  await fill('Reserved zone', 'zone')
  await fill('Hostname label', ' Fresh.Alias ')
}
async function domainHint() {
  await act(async () =>
    liveConnection.changed({
      version: 1,
      topic: 'domains',
      tenantId: 'alpha',
      resourceId: 'other-binding',
    }),
  )
  await act(async () => vi.advanceTimersByTimeAsync(250))
}

it.each(['zones', 'bindings', 'both'] as const)(
  'keeps a loaded alias form actionable during held %s push reads and submits exactly one immutable scoped command',
  async (heldRead) => {
    await aliasDraft()
    const input = field('Hostname label')
    expect(attachButton().disabled).toBe(false)
    const zonesRead = deferred<Response>()
    const bindingsRead = deferred<Response>()
    const accepted = deferred<Response>()
    if (heldRead !== 'bindings') heldZones = zonesRead.promise
    if (heldRead !== 'zones') heldBindings = bindingsRead.promise
    heldAttach = accepted.promise
    const before = fetchMock.mock.calls.length
    await domainHint()
    expect(
      fetchMock.mock.calls
        .slice(before)
        .some(
          ([url, init]) =>
            String(url).endsWith('/domain-zones') && (init?.method ?? 'GET') === 'GET',
        ),
    ).toBe(true)
    expect(
      fetchMock.mock.calls
        .slice(before)
        .some(
          ([url, init]) =>
            String(url).endsWith('/deployments/dep/hostnames') && (init?.method ?? 'GET') === 'GET',
        ),
    ).toBe(true)
    expect(field('Hostname label')).toBe(input)
    expect(input.value).toBe(' Fresh.Alias ')
    expect.soft(attachButton().disabled).toBe(false)
    await click('Attach alias')
    expect(aliasPosts()).toHaveLength(1)
    const [url, command] = aliasPosts()[0]
    const submitted = command?.body
    expect(String(url)).toContain('/web-services/app/deployments/dep/hostnames')
    expect(JSON.parse(submitted as string)).toEqual({
      tenantDomainZoneId: 'zone',
      relativeName: 'fresh.alias',
    })
    expect(new Headers(command?.headers).get('X-Pole-Tenant-ID')).toBe('alpha')
    expect(new Headers(command?.headers).get('Authorization')).toBe('Bearer token')
    expect(attachButton().disabled).toBe(true)
    await act(async () =>
      container
        .querySelector('form')!
        .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })),
    )
    expect(aliasPosts()).toHaveLength(1)
    heldZones = undefined
    heldBindings = undefined
    await act(async () => {
      zonesRead.resolve(new Response(JSON.stringify(zoneRows)))
      bindingsRead.resolve(new Response(JSON.stringify(rows)))
    })
    expect(command?.body).toBe(submitted)
    expect(aliasPosts()).toHaveLength(1)
    heldAttach = undefined
    await act(async () => accepted.resolve(new Response(JSON.stringify(service.deployments[0]))))
    expect(field('Hostname label').value).toBe('')
    expect(rows.filter((row) => row.hostname === 'fresh.alias.company.apps.test')).toHaveLength(1)
  },
)

it.each(['zones', 'bindings'] as const)(
  'blocks an alias after failed %s validation and through a held retry until fresh evidence succeeds',
  async (failedRead) => {
    await aliasDraft()
    zonesFail = failedRead === 'zones'
    bindingsFail = failedRead === 'bindings'
    await domainHint()
    expect(attachButton().disabled).toBe(true)
    expect(field('Hostname label').value).toBe(' Fresh.Alias ')
    await act(async () =>
      container
        .querySelector('form')!
        .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })),
    )
    expect(aliasPosts()).toHaveLength(0)
    const retried = deferred<Response>()
    if (failedRead === 'zones') heldZones = retried.promise
    else heldBindings = retried.promise
    zonesFail = false
    bindingsFail = false
    await domainHint()
    expect(attachButton().disabled).toBe(true)
    await click('Attach alias')
    expect(aliasPosts()).toHaveLength(0)
    heldZones = undefined
    heldBindings = undefined
    await act(async () =>
      retried.resolve(new Response(JSON.stringify(failedRead === 'zones' ? zoneRows : rows))),
    )
    await act(async () => vi.advanceTimersByTimeAsync(1))
    expect(attachButton().disabled).toBe(false)
    await click('Attach alias')
    expect(aliasPosts()).toHaveLength(1)
  },
)

it('blocks a draft when accepted push inventory removes its actual reserved zone', async () => {
  await aliasDraft()
  zoneRows = []
  await domainHint()
  expect(attachButton().disabled).toBe(true)
  expect(field('Hostname label').value).toBe(' Fresh.Alias ')
  expect(text()).toContain('Reserve a tenant zone on this target')
  await act(async () =>
    container
      .querySelector('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })),
  )
  expect(aliasPosts()).toHaveLength(0)
})

it('keeps initial absent metadata, invalid labels and duplicate aliases blocked before any command', async () => {
  vi.useFakeTimers()
  window.history.replaceState({}, '', '/?view=web-services')
  const zonesRead = deferred<Response>()
  const bindingsRead = deferred<Response>()
  heldZones = zonesRead.promise
  heldBindings = bindingsRead.promise
  await render('Owner', 'alpha', 'web-services', 'token', true)
  await act(async () => vi.advanceTimersByTimeAsync(500))
  expect(attachButton().disabled).toBe(true)
  await act(async () =>
    container
      .querySelector('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })),
  )
  expect(aliasPosts()).toHaveLength(0)
  heldZones = undefined
  heldBindings = undefined
  await act(async () => {
    zonesRead.resolve(new Response(JSON.stringify(zoneRows)))
    bindingsRead.resolve(new Response(JSON.stringify(rows)))
  })
  await act(async () => vi.advanceTimersByTimeAsync(1))
  await fill('Reserved zone', 'zone')
  for (const name of ['', 'bad_name', '.api', 'api']) {
    await fill('Hostname label', name)
    expect(attachButton().disabled).toBe(true)
    await click('Attach alias')
    expect(aliasPosts()).toHaveLength(0)
  }
  await fill('Hostname label', 'valid')
  expect(attachButton().disabled).toBe(false)
})

const reservationDeletes = () =>
  fetchMock.mock.calls.filter(
    ([url, init]) => String(url).endsWith('/domain-zones/zone') && init?.method === 'DELETE',
  )
const reservationButton = () =>
  [...container.querySelectorAll<HTMLButtonElement>('button')].find(
    (item) => visible(item) && item.textContent === 'Remove reservation',
  )!
const reservationConfirm = () =>
  [...container.querySelectorAll<HTMLButtonElement>('dialog[open] button')].find(
    (item) => item.textContent === 'Remove reservation',
  )!
function ownerSnapshot(pageNumber = 1) {
  return {
    items: ownerCount
      ? [
          {
            binding: custom,
            resourceDisplayName: 'Application',
            resourceGroupId: 'group',
            placement: service.deployments[1],
          },
        ]
      : [],
    totalCount: ownerCount,
    pageNumber,
    pageSize: 25,
  }
}
async function removableReservation() {
  vi.useFakeTimers()
  ownerCount = 0
  await render('Owner', 'alpha', 'domains', 'token', true)
  await click(zone.domain)
  await act(async () => vi.advanceTimersByTimeAsync(500))
}

it.each(['zone', 'owners', 'both'] as const)(
  'removes the exact loaded reservation during held %s push reads without treating fetch activity as missing ownership evidence',
  async (heldRead) => {
    await removableReservation()
    const zoneRead = deferred<Response>()
    const ownerRead = deferred<Response>()
    if (heldRead !== 'owners') heldZone = zoneRead.promise
    if (heldRead !== 'zone') heldOwners = ownerRead.promise
    await domainHint()
    expect.soft(reservationButton().disabled).toBe(false)
    await click('Remove reservation')
    expect(container.querySelector('dialog[open]')).not.toBeNull()
    await click('Remove reservation')
    expect(reservationDeletes()).toHaveLength(1)
    const [url, command] = reservationDeletes()[0]
    expect(String(url)).toBe('http://localhost/api/v1/domain-zones/zone')
    expect(new Headers(command?.headers).get('X-Pole-Tenant-ID')).toBe('alpha')
    expect(new Headers(command?.headers).get('Authorization')).toBe('Bearer token')
    expect(command?.body).toBeUndefined()
    heldZone = undefined
    heldOwners = undefined
    await act(async () => {
      zoneRead.resolve(new Response(JSON.stringify(currentZone)))
      ownerRead.resolve(new Response(JSON.stringify(ownerSnapshot())))
    })
  },
)

it.each(['zone', 'owners'] as const)(
  'blocks an open reservation removal after a %s read failure and throughout its held retry',
  async (failedRead) => {
    await removableReservation()
    await click('Remove reservation')
    zoneFail = failedRead === 'zone'
    ownersFail = failedRead === 'owners'
    await domainHint()
    expect.soft(reservationConfirm().disabled).toBe(true)
    await click('Remove reservation')
    expect(reservationDeletes()).toHaveLength(0)
    zoneFail = false
    ownersFail = false
    const read = deferred<Response>()
    if (failedRead === 'zone') heldZone = read.promise
    else heldOwners = read.promise
    await domainHint()
    expect(reservationConfirm().disabled).toBe(true)
    await click('Remove reservation')
    expect(reservationDeletes()).toHaveLength(0)
    heldZone = undefined
    heldOwners = undefined
    await act(async () =>
      read.resolve(
        new Response(JSON.stringify(failedRead === 'zone' ? currentZone : ownerSnapshot())),
      ),
    )
    expect(reservationConfirm().disabled).toBe(false)
  },
)

it.each(['owners', 'revision', 'kind'] as const)(
  'does not retarget an open reservation removal when current %s metadata changes',
  async (changedField) => {
    await removableReservation()
    await click('Remove reservation')
    if (changedField === 'owners') ownerCount = 1
    if (changedField === 'revision') currentZone = { ...currentZone, revision: 2 }
    if (changedField === 'kind') currentZone = { ...currentZone, kind: 'generated' }
    await domainHint()
    expect.soft(reservationConfirm().disabled).toBe(true)
    await click('Remove reservation')
    expect(reservationDeletes()).toHaveLength(0)
  },
)

it('requires initial owner evidence and current tenant management rights for reservation removal', async () => {
  ownerCount = 0
  const read = deferred<Response>()
  heldOwners = read.promise
  await render()
  await click(zone.domain)
  expect(reservationButton().disabled).toBe(true)
  heldOwners = undefined
  await act(async () => read.resolve(new Response(JSON.stringify(ownerSnapshot()))))
  await click('Remove reservation')
  await render('Viewer')
  expect(reservationConfirm()?.disabled ?? true).toBe(true)
  expect(reservationDeletes()).toHaveLength(0)
  await render('Owner', 'beta')
  expect(container.querySelector('dialog[open]')).toBeNull()
  expect(reservationDeletes()).toHaveLength(0)
})
