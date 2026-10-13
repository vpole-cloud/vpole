// @vitest-environment jsdom
import { act, useLayoutEffect, useState } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
const { webcrypto } = await vi.importActual<{ webcrypto: Crypto }>('node:crypto')
import { fetchAvailableStorageProfiles, fetchStorageProfiles } from '../../api'
import { SessionQueries } from '../../data/SessionQueries'
import { scopedQueryKey, useQuerySession, type QuerySession } from '../../data/querySession'
import { useApiScope } from '../../data/useApiScope'
import { useScopedQuery } from '../../data/useScopedQuery'
import { useTenants } from '../../hooks/useTenants'
import { RealtimeProvider } from '../../realtime/RealtimeProvider'
import { createRealtimeConnection } from '../../realtime/connection'
import { useRealtime } from '../../realtime/context'
import type { RealtimeController } from '../../realtime/controller'
import { createRealtimeSession } from '../../realtime/sessionApi'
import { AppShell } from '../../shell/AppShell'
import { createAppRegistry } from '../../shell/moduleRegistry'
import { FakeConnection } from '../../test/fakeRealtimeConnection'
import { deferred, strictFetch } from '../../test/http'
import type { StorageProfile } from '../../types'
import { StorageView } from './PlatformViews'

vi.mock('../../realtime/connection', () => ({ createRealtimeConnection: vi.fn() }))
vi.mock('../../realtime/sessionApi', () => ({ createRealtimeSession: vi.fn(), revokeRealtimeSession: vi.fn(async () => {}) }))
const profile: StorageProfile = { id: 'disk', displayName: 'Private profile', description: '', mediaType: 'SSD', performanceTier: 'Standard', isEnabled: true, isDefault: true,
  targetMappings: [{ deploymentTargetId: 'target', storageClassName: 'fast' }], revision: 1, allowedResourceTypes: ['pole/redis'], tenantAccess: { mode: 'AllTenants', tenantIds: [] } }
const http = strictFetch()
let root: Root
let container: HTMLDivElement
let connections: FakeConnection[]
let privateProfile: StorageProfile
let publicProfile: StorageProfile
let storageConnectionVersion = 1
let storageObservationVersion = 1
let pendingDiscovery: Promise<Response> | undefined
let pendingRead: Promise<Response> | undefined
let pendingWrite: Promise<Response> | undefined
let pendingTenants: Promise<Response> | undefined
let readSignal: AbortSignal | undefined
let writeSignal: AbortSignal | undefined
let controller!: RealtimeController
let session!: QuerySession
let publicState!: ReturnType<typeof useScopedQuery<StorageProfile[]>>
let tenantDraft = ''
let editTenant!: (value: string) => void
let hidden = false
let workspaceDraft = ''
let editWorkspace!: (value: string) => void
let workspaceMounts = 0
let retainedPrivateRefresh: () => Promise<StorageProfile[] | null>
let retainedPrivateCredential: () => string | undefined
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  vi.useFakeTimers(); vi.clearAllMocks(); localStorage.clear(); http.reset()
  vi.stubGlobal('crypto', webcrypto)
  hidden = false; Object.defineProperty(document, 'hidden', { configurable: true, get: () => hidden })
  window.dispatchEvent(new Event('pageshow'))
  Object.defineProperties(HTMLDialogElement.prototype, {
    showModal: { configurable: true, value: function (this: HTMLDialogElement) { this.open = true } },
    close: { configurable: true, value: function (this: HTMLDialogElement) { this.open = false } },
  })
  window.history.replaceState({}, '', '/')
  container = document.createElement('div'); document.body.append(container); root = createRoot(container)
  privateProfile = { ...profile }; publicProfile = { ...profile, displayName: 'Tenant profile' }
  storageConnectionVersion = 1; storageObservationVersion = 1; pendingDiscovery = undefined; pendingRead = undefined; pendingWrite = undefined; pendingTenants = undefined; readSignal = undefined; writeSignal = undefined; connections = []; workspaceMounts = 0
  vi.mocked(createRealtimeConnection).mockImplementation(() => { const connection = new FakeConnection(); connections.push(connection); return connection })
  vi.mocked(createRealtimeSession).mockResolvedValue({ expiresAtUtc: new Date(Date.now() + 300000).toISOString() })
  const json = (value: unknown) => Promise.resolve(new Response(JSON.stringify(value)))
  http.fetch.mockImplementation((input, init) => {
    const url = new URL(String(input), 'http://localhost')
    const method = init?.method ?? 'GET'
    if (method === 'PUT' && url.pathname === '/api/api/v1/platform/storage/profiles/disk') { writeSignal = init?.signal ?? undefined; return pendingWrite ?? json(privateProfile) }
    if (method !== 'GET') return http.unhandled(input, init)
    if (url.pathname === '/api/api/v1/tenants') return pendingTenants ?? json([{ id: 'alpha', name: 'Alpha', role: 'Member', isDefault: true, connection: { kind: 'Kubeconfig', version: storageConnectionVersion } }])
    if (url.pathname === '/api/api/v1/platform/storage/profiles') { readSignal = init?.signal ?? undefined; return pendingRead ?? json([privateProfile]) }
    if (url.pathname === '/api/api/v1/platform/deployment-targets') return json([{ id: 'target', displayName: 'Target', enabled: true, acceptsWorkloads: true, isDefault: true, connection: { kind: 'Kubeconfig', version: storageConnectionVersion } }])
    if (url.pathname === '/api/api/v1/storage-profiles') return json([publicProfile])
    if (url.pathname === '/api/api/v1/platform/storage/observation') return pendingDiscovery ?? json({ deploymentTargetId: 'target', connectionVersion: storageConnectionVersion, observationVersion: storageObservationVersion, checkedAtUtc: new Date().toISOString(), complete: true, storageClasses: [{ name: 'fast', provisioner: 'test', isDefault: true }], persistentVolumes: [], persistentVolumeClaims: [] })
    return http.unhandled(input, init)
  })
  vi.stubGlobal('fetch', http.fetch)
})
afterEach(async () => { await act(async () => root.unmount()); container.remove(); window.dispatchEvent(new Event('pageshow')); http.verify(); vi.unstubAllGlobals(); vi.useRealTimers() })
function TenantDraftView() {
  const [draft, setDraft] = useState('Saved workspace draft')
  useLayoutEffect(() => { workspaceMounts++ }, [])
  useLayoutEffect(() => { workspaceDraft = draft; editWorkspace = setDraft }, [draft])
  return <input aria-label="Tenant workspace draft" value={draft} onChange={event => setDraft(event.target.value)} />
}
const registry = createAppRegistry([{ id: 'storage', views: [
  { id: 'storage', label: 'Storage', component: StorageView, access: 'authenticated' },
  { id: 'tenant-draft', label: 'Tenant draft', component: TenantDraftView, access: 'tenant' },
] }])
function PrivateProbe({ token, enabled }: { token: string; enabled: boolean }) {
  const origin = useApiScope({ baseUrl: '/api', token, privileged: true })
  const query = useScopedQuery({ scope: ['/api', undefined], key: ['platform-storage', 'retained-probe'], enabled, privileged: true, realtime: [],
    load: signal => fetchStorageProfiles('/api', origin.credential(), signal) })
  useLayoutEffect(() => { retainedPrivateRefresh = query.refresh; retainedPrivateCredential = origin.credential }, [query.refresh, origin.credential])
  return null
}
function Frame({ token, platformAdminClaim, probe }: { token: string; platformAdminClaim: boolean; probe: boolean }) {
  const realtime = useRealtime()!
  const queries = useQuerySession()
  const tenants = useTenants('/api', token, true, 'user')
  const [draft, setDraft] = useState('Saved tenant draft')
  const origin = useApiScope({ baseUrl: '/api', tenantId: 'alpha', token })
  const storage = useScopedQuery({ scope: ['/api', 'alpha'], key: ['eligibility', 'storage-profiles', 'pole/redis', 'target'], realtime: [{ topic: 'storage-eligibility', tenantId: 'alpha' }],
    load: signal => fetchAvailableStorageProfiles('/api', 'pole/redis', 'target', origin.credential(), 'alpha', signal) })
  useLayoutEffect(() => { controller = realtime; session = queries; tenantDraft = draft; editTenant = setDraft; publicState = storage }, [realtime, queries, draft, storage])
  return <>{probe && <PrivateProbe token={token} enabled={platformAdminClaim} />}<AppShell registry={registry} initialViewId="storage" api={{ baseUrl: '/api', token }} tenants={tenants}
    auth={{ initialized: true, authenticated: true, userId: 'user', isPlatformAdmin: platformAdminClaim && realtime.platformAllowed('/api'), login: () => {}, logout: () => {} }} /></>
}
const render = (token = 'first', platformAdminClaim = true, probe = false) => root.render(<SessionQueries identity="/api:true:user"><RealtimeProvider baseUrl="/api" token={token} platformAdminClaim={platformAdminClaim} enabled userId="user"><Frame token={token} platformAdminClaim={platformAdminClaim} probe={probe} /></RealtimeProvider></SessionQueries>)
const button = (name: string) => [...container.querySelectorAll<HTMLButtonElement>('button')].find(item => item.textContent === name)!
const field = () => [...container.querySelectorAll('label')].find(item => item.querySelector('.field__label')?.textContent === 'Display name')?.querySelector<HTMLInputElement>('input')
async function fill(value: string) { await act(async () => { const input = field()!; Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(input, value); input.dispatchEvent(new Event('input', { bubbles: true })) }) }
const privateReads = () => http.fetch.mock.calls.filter(([url]) => String(url).endsWith('/platform/storage/profiles')).length

it('refreshes private storage metadata and public tenant choices independently without replacing a dirty profile draft', async () => {
  await act(async () => render()); await act(async () => vi.advanceTimersByTimeAsync(500))
  await act(async () => button('Edit').click()); await fill('Dirty profile')
  privateProfile = { ...privateProfile, displayName: 'Committed profile', revision: 2 }
  await act(async () => connections[0].changed({ version: 1, topic: 'storage-profiles', resourceId: 'disk' }))
  await act(async () => vi.advanceTimersByTimeAsync(250))
  expect(field()?.value).toBe('Dirty profile'); expect(container.textContent).toContain('This profile changed on the server')
  const reads = privateReads()
  publicProfile = { ...publicProfile, displayName: 'Committed tenant choice' }
  await act(async () => connections[0].changed({ version: 1, topic: 'storage-eligibility', tenantId: 'alpha' }))
  await act(async () => vi.advanceTimersByTimeAsync(250))
  expect(publicState.resource?.[0].displayName).toBe('Committed tenant choice'); expect(privateReads()).toBe(reads)
  const publicRead = http.fetch.mock.calls.find(([url]) => String(url).includes('/api/v1/storage-profiles?'))!
  expect(new Headers(publicRead[1]?.headers).get('X-Pole-Tenant-ID')).toBe('alpha')
  const privateRead = http.fetch.mock.calls.find(([url]) => String(url).endsWith('/platform/storage/profiles'))!
  expect(new Headers(privateRead[1]?.headers).has('X-Pole-Tenant-ID')).toBe(false)
  await act(async () => { hidden = true; document.dispatchEvent(new Event('visibilitychange')) })
  http.fetch.mockClear()
  await act(async () => vi.advanceTimersByTimeAsync(120000))
  expect(http.fetch).not.toHaveBeenCalled()
})

it('clears privileged data and a held write/read on denial, then restores only fresh admin state without changing tenant drafts or session', async () => {
  await act(async () => render()); await act(async () => vi.advanceTimersByTimeAsync(500))
  const originalSession = session
  await act(async () => editTenant('Unsaved tenant draft'))
  await act(async () => button('Edit').click()); await fill('Private draft')
  const write = deferred<Response>(); pendingWrite = write.promise
  await act(async () => field()!.closest('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })))
  expect(writeSignal).toBeDefined()
  const read = deferred<Response>(); pendingRead = read.promise
  const inventoryKey = scopedQueryKey(session, ['/api', undefined], ['platform-storage', 'inventory'])
  let reading!: Promise<void>
  await act(async () => { reading = session.client.refetchQueries({ queryKey: inventoryKey, exact: true }, { cancelRefetch: false }) })
  await act(async () => connections[0].accessRevoked())
  expect(writeSignal?.aborted).toBe(true); expect(readSignal?.aborted).toBe(true)
  expect(field()).toBeUndefined(); expect(container.querySelector('dialog')).toBeNull()
  expect(session.client.getQueryData(inventoryKey)).toBeUndefined()
  expect(publicState.resource?.[0].displayName).toBe('Tenant profile')
  expect(controller.covered({ topic: 'tenant-access', resourceId: 'user' })).toBe(true)
  pendingRead = undefined; pendingWrite = undefined
  await act(async () => render('second')); await act(async () => vi.advanceTimersByTimeAsync(500))
  expect(controller.platformAllowed('/api')).toBe(true)
  expect(session).toBe(originalSession); expect(tenantDraft).toBe('Unsaved tenant draft')
  expect(field()?.value).toBe(''); expect(container.querySelector('dialog')).toBeNull()
  expect(button('New profile').disabled).toBe(false)
  expect(field()?.closest('fieldset')?.disabled).toBe(false)
  await act(async () => { read.resolve(new Response(JSON.stringify([{ ...privateProfile, displayName: 'Late private data' }]))); write.resolve(new Response(JSON.stringify(privateProfile))); await reading })
  expect(container.textContent).not.toContain('Late private data')
})

it('retains the actual tenant workspace while platform denial revalidates a held discovery response', async () => {
  window.history.replaceState({}, '', '/?view=tenant-draft')
  await act(async () => render()); await act(async () => vi.advanceTimersByTimeAsync(500))
  await act(async () => editWorkspace('Unsaved workspace'))
  const discovery = deferred<Response>(); pendingTenants = discovery.promise
  await act(async () => connections[0].accessRevoked())
  expect(workspaceDraft).toBe('Unsaved workspace')
  expect(container.querySelector<HTMLInputElement>('[aria-label="Tenant workspace draft"]')?.value).toBe('Unsaved workspace')
  expect(workspaceMounts).toBe(1)
  await act(async () => discovery.resolve(new Response(JSON.stringify([{ id: 'alpha', name: 'Alpha', role: 'Member', isDefault: true, connection: { kind: 'Kubeconfig', version: storageConnectionVersion } }]))))
  expect(workspaceMounts).toBe(1); expect(workspaceDraft).toBe('Unsaved workspace')
})

it('clears private caches and late work on credential claim loss without a server denial, preserving tenant state and fencing retained callbacks', async () => {
  await act(async () => render('first', true, true)); await act(async () => vi.advanceTimersByTimeAsync(500))
  const originalSession = session; const refresh = retainedPrivateRefresh; const credential = retainedPrivateCredential
  await act(async () => editTenant('Unsaved tenant draft'))
  await act(async () => button('Edit').click()); await fill('Private draft')
  const write = deferred<Response>(); pendingWrite = write.promise
  await act(async () => field()!.closest('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })))
  const read = deferred<Response>(); pendingRead = read.promise
  const inventoryKey = scopedQueryKey(session, ['/api', undefined], ['platform-storage', 'inventory'])
  const probeKey = scopedQueryKey(session, ['/api', undefined], ['platform-storage', 'retained-probe'])
  let reading!: Promise<void>
  await act(async () => { reading = session.client.refetchQueries({ queryKey: inventoryKey, exact: true }, { cancelRefetch: false }) })
  // Simulate the SDK's new readonly credential; no AccessRevoked message is emitted.
  await act(async () => render('readonly', false, true)); await act(async () => vi.advanceTimersByTimeAsync(500))
  expect(controller.platformAllowed('/api')).toBe(false)
  expect(readSignal?.aborted).toBe(true); expect(writeSignal?.aborted).toBe(true)
  expect(session.client.getQueryData(inventoryKey)).toBeUndefined(); expect(session.client.getQueryData(probeKey)).toBeUndefined()
  expect(session).toBe(originalSession); expect(tenantDraft).toBe('Unsaved tenant draft')
  expect(publicState.resource?.[0].displayName).toBe('Tenant profile')
  expect(controller.covered({ topic: 'tenant-access', resourceId: 'user' })).toBe(true)
  expect(field()).toBeUndefined()
  const count = privateReads()
  await act(async () => { expect(await refresh()).toBeNull() })
  expect(() => credential()).toThrow('Aborted'); expect(privateReads()).toBe(count)
  pendingRead = undefined; pendingWrite = undefined
  await act(async () => { read.resolve(new Response(JSON.stringify([{ ...privateProfile, displayName: 'Late private canary' }]))); write.resolve(new Response(JSON.stringify(privateProfile))); await reading })
  expect(session.client.getQueryData(inventoryKey)).toBeUndefined(); expect(container.textContent).not.toContain('Late private canary')
  await act(async () => render('first', true, true)); await act(async () => vi.advanceTimersByTimeAsync(500))
  expect(controller.platformAllowed('/api')).toBe(false); expect(session.client.getQueryData(inventoryKey)).toBeUndefined()
  await act(async () => render('fresh-admin', true, true)); await act(async () => vi.advanceTimersByTimeAsync(500))
  expect(controller.platformAllowed('/api')).toBe(true); expect(tenantDraft).toBe('Unsaved tenant draft')
  expect(field()?.value).toBe(''); expect(session).toBe(originalSession)
})

it('preserves the dirty form and disables saving while a native hint and connection rotation resync storage', async () => {
  await act(async () => render()); await act(async () => vi.advanceTimersByTimeAsync(1000))
  await act(async () => button('Edit').click()); await fill('Dirty retained profile')
  const original = field()!; const held = deferred<Response>(); pendingDiscovery = held.promise
  storageObservationVersion = 2
  await act(async () => connections[0].changed({ version: 1, topic: 'storage-observations', resourceId: 'target', revision: 1, observationVersion: 2 }))
  await act(async () => vi.advanceTimersByTimeAsync(250))
  expect(field()).toBe(original); expect(field()?.value).toBe('Dirty retained profile'); expect(button('Save changes').disabled).toBe(true)
  storageConnectionVersion = 2; storageObservationVersion = 3
  await act(async () => connections[0].changed({ version: 1, topic: 'deployment-targets', resourceId: 'target', revision: 100 }))
  await act(async () => vi.advanceTimersByTimeAsync(250))
  expect(field()).toBe(original); expect(field()?.value).toBe('Dirty retained profile'); expect(button('Save changes').disabled).toBe(true)
  pendingDiscovery = undefined
  await act(async () => held.resolve(new Response(JSON.stringify({ deploymentTargetId: 'target', connectionVersion: 1, observationVersion: 2, checkedAtUtc: new Date().toISOString(), complete: true, storageClasses: [], persistentVolumes: [], persistentVolumeClaims: [] }))))
  // Read the new connection context after releasing the deliberately stale response.
  await act(async () => session.client.invalidateQueries({ predicate: query => query.queryKey[6] === 'discovery', refetchType: 'active' }))
  await act(async () => vi.advanceTimersByTimeAsync(1000))
  expect(field()).toBe(original); expect(field()?.value).toBe('Dirty retained profile'); expect(button('Save changes').disabled).toBe(false)
  await act(async () => render('renewed')); await act(async () => vi.advanceTimersByTimeAsync(1000))
  expect(field()).toBe(original); expect(field()?.value).toBe('Dirty retained profile')
})
