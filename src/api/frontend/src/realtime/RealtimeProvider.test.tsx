// @vitest-environment jsdom
import { act, useLayoutEffect, useState } from 'react'
import type { Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { ApiError } from '../api/transport'
import { useApiScope } from '../data/useApiScope'
import { useScopedQuery } from '../data/useScopedQuery'
import { useVisibleRefresh } from '../hooks/useVisibleRefresh'
import { createTestApp } from '../test/TestApp'
import { FakeConnection } from '../test/fakeRealtimeConnection'
import { deferred } from '../test/http'
import { createRealtimeConnection } from './connection'
import { createRealtimeSession, revokeRealtimeSession } from './sessionApi'
import { RealtimeProvider } from './RealtimeProvider'
import { useRealtime } from './context'
import type { RealtimeController } from './controller'
import { resourceInterests } from './resourceInterests'

vi.mock('./connection', () => ({ createRealtimeConnection: vi.fn() }))
vi.mock('./sessionApi', () => ({ createRealtimeSession: vi.fn(), revokeRealtimeSession: vi.fn(async () => {}) }))
let root: Root
let hidden = false
let connections: FakeConnection[]
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  vi.useFakeTimers(); vi.clearAllMocks()
  window.dispatchEvent(new Event('pageshow'))
  hidden = false
  Object.defineProperty(document, 'hidden', { configurable: true, get: () => hidden })
  root = createTestApp().root; connections = []
  vi.mocked(createRealtimeConnection).mockImplementation(() => { const connection = new FakeConnection(); connections.push(connection); return connection })
  vi.mocked(createRealtimeSession).mockResolvedValue({ expiresAtUtc: new Date(Date.now() + 300000).toISOString() })
})
afterEach(async () => { await act(async () => root.unmount()); window.dispatchEvent(new Event('pageshow')); vi.useRealTimers() })
const interest = [{ topic: 'source-builds' as const, tenantId: 'tenant', resourceId: 'service' }]

it('bootstraps the current credential and reconnects while preserving drafts, workspace state and authoritative query envelopes', async () => {
  let mounts = 0
  let controller: RealtimeController | null = null
  let state!: ReturnType<typeof useScopedQuery<string>>
  let origin!: ReturnType<typeof useApiScope>
  let edit!: (value: string) => void
  let draft = ''
  function Probe({ token }: { token: string }) {
    const [value, setValue] = useState('draft'); const [tab] = useState('configuration')
    const query = useScopedQuery({ scope: ['api', 'tenant'], key: ['source-builds', 'jobs', 'service'], realtime: interest, load: async () => 'server' })
    const apiScope = useApiScope({ baseUrl: 'api', tenantId: 'tenant', token })
    const realtime = useRealtime()
    useLayoutEffect(() => { mounts++ }, [])
    useLayoutEffect(() => { state = query; origin = apiScope; controller = realtime; edit = setValue; draft = `${tab}:${value}` }, [query, apiScope, realtime, tab, value])
    return null
  }
  const render = (token: string) => root.render(<RealtimeProvider baseUrl="api" token={token} enabled><Probe token={token} /></RealtimeProvider>)
  await act(async () => render('credential-one'))
  await act(async () => edit('unsaved'))
  const client = origin.session.client; const key = state.queryKey
  await act(async () => render('credential-two'))
  expect(createRealtimeSession).toHaveBeenLastCalledWith('api', 'credential-two', expect.any(AbortSignal))
  expect(connections).toHaveLength(2)
  expect(connections[0].stop).toHaveBeenCalled()
  expect(mounts).toBe(1)
  expect(draft).toBe('configuration:unsaved')
  expect(origin.credential()).toBe('credential-two')
  expect(origin.session.client).toBe(client)
  expect(client.getQueryData(key)).toEqual({ value: 'server', identity: JSON.stringify(key) })
  await act(async () => controller!.logout())
  expect(revokeRealtimeSession).toHaveBeenCalledWith('api')
  expect(origin.isCurrent()).toBe(false)
  expect(state.resource).toBeNull()
  await act(async () => render('credential-three'))
  expect(createRealtimeSession).toHaveBeenCalledTimes(2)
})

it('bounds renewal for an unchanged near-expiry JWT while retaining dirty state until credential rotation', async () => {
  const expiresAt = Date.now() + 40000
  vi.mocked(createRealtimeSession).mockImplementation(async () => {
    if (Date.now() >= expiresAt) throw new ApiError('Expired', 401)
    return { expiresAtUtc: new Date(expiresAt).toISOString() }
  })
  let mounts = 0
  let dirty = ''
  let edit!: (value: string) => void
  let query!: ReturnType<typeof useScopedQuery<string>>
  let origin!: ReturnType<typeof useApiScope>
  function Probe({ token }: { token: string }) {
    const [draft, setDraft] = useState('saved')
    const [tab] = useState('configuration')
    const state = useScopedQuery({ scope: ['api', 'tenant'], key: ['source-builds', 'jobs', 'service'], realtime: interest, load: async () => 'server' })
    const api = useApiScope({ baseUrl: 'api', tenantId: 'tenant', token })
    useLayoutEffect(() => { mounts++ }, [])
    useLayoutEffect(() => { edit = setDraft; dirty = `${tab}:${draft}`; query = state; origin = api }, [tab, draft, state, api])
    return null
  }
  const render = (token: string) => root.render(<RealtimeProvider baseUrl="api" token={token} enabled><Probe token={token} /></RealtimeProvider>)
  await act(async () => render('unchanged'))
  await act(async () => edit('unsaved'))
  const client = origin.session.client
  await act(async () => vi.advanceTimersByTimeAsync(30000))
  expect(createRealtimeSession).toHaveBeenCalledTimes(2)
  expect(connections).toHaveLength(2)
  await act(async () => vi.advanceTimersByTimeAsync(9999))
  expect(createRealtimeSession).toHaveBeenCalledTimes(2)
  expect(dirty).toBe('configuration:unsaved')
  expect(client.getQueryData(query.queryKey)).toEqual({ value: 'server', identity: JSON.stringify(query.queryKey) })
  await act(async () => vi.advanceTimersByTimeAsync(1))
  expect(createRealtimeSession).toHaveBeenCalledTimes(3)
  expect(connections).toHaveLength(2)
  expect(query.resource).toBe('server')
  vi.mocked(createRealtimeSession).mockResolvedValue({ expiresAtUtc: new Date(Date.now() + 40000).toISOString() })
  await act(async () => render('rotated'))
  expect(createRealtimeSession).toHaveBeenCalledTimes(4)
  expect(createRealtimeSession).toHaveBeenLastCalledWith('api', 'rotated', expect.any(AbortSignal))
  expect(connections).toHaveLength(3)
  expect(origin.session.client).toBe(client)
  expect(mounts).toBe(1)
  expect(dirty).toBe('configuration:unsaved')
})

it('uses acknowledged 60-second safety reads, falls back to the original interval when disconnected, and releases hidden interests without GETs', async () => {
  const load = vi.fn(async () => 'server')
  function Probe() {
    const query = useScopedQuery({ scope: ['api', 'tenant'], key: ['source-builds', 'jobs', 'service'], realtime: interest, load })
    useVisibleRefresh(true, query.refresh, 2500, interest)
    return null
  }
  await act(async () => root.render(<RealtimeProvider baseUrl="api" token="credential" enabled><Probe /></RealtimeProvider>))
  await act(async () => vi.advanceTimersByTimeAsync(250))
  load.mockClear()
  await act(async () => vi.advanceTimersByTimeAsync(30000))
  expect(load).not.toHaveBeenCalled()
  await act(async () => vi.advanceTimersByTimeAsync(30000))
  expect(load).toHaveBeenCalledTimes(1)
  await act(async () => connections[0].reconnecting())
  load.mockClear()
  await act(async () => vi.advanceTimersByTimeAsync(2500))
  expect(load).toHaveBeenCalledTimes(1)
  await act(async () => { hidden = true; document.dispatchEvent(new Event('visibilitychange')) })
  expect(connections[0].stop).toHaveBeenCalled()
  load.mockClear()
  await act(async () => vi.advanceTimersByTimeAsync(60000))
  expect(load).not.toHaveBeenCalled()
  await act(async () => { hidden = false; document.dispatchEvent(new Event('visibilitychange')) })
  await act(async () => vi.advanceTimersByTimeAsync(250))
  expect(load).toHaveBeenCalled()
  const current = connections.at(-1)!
  await act(async () => { hidden = true; document.dispatchEvent(new Event('visibilitychange')) })
  expect(current.stop).toHaveBeenCalled()
})

it('resyncs page restoration and online recovery, fences hidden refresh callbacks, and follows API identity without a token key', async () => {
  const load = vi.fn(async () => 'server')
  let refresh!: () => Promise<string | null>
  function Probe({ baseUrl }: { baseUrl: string }) {
    const query = useScopedQuery({ scope: [baseUrl, 'tenant'], key: ['source-builds', 'jobs', 'service'], realtime: interest, load })
    useLayoutEffect(() => { refresh = query.refresh })
    return null
  }
  const render = (baseUrl: string) => root.render(<RealtimeProvider baseUrl={baseUrl} token="current" enabled><Probe baseUrl={baseUrl} /></RealtimeProvider>)
  await act(async () => render('api'))
  await act(async () => vi.advanceTimersByTimeAsync(250))
  const oldRefresh = refresh
  await act(async () => window.dispatchEvent(new Event('pagehide')))
  load.mockClear()
  await act(async () => { expect(await oldRefresh()).toBeNull(); window.dispatchEvent(new Event('online')) })
  await act(async () => vi.advanceTimersByTimeAsync(60000))
  expect(load).not.toHaveBeenCalled()
  expect(createRealtimeSession).toHaveBeenCalledTimes(1)
  await act(async () => window.dispatchEvent(new Event('pageshow')))
  await act(async () => vi.advanceTimersByTimeAsync(250))
  expect(load).toHaveBeenCalled()
  expect(createRealtimeSession).toHaveBeenCalledTimes(2)
  await act(async () => window.dispatchEvent(new Event('online')))
  expect(createRealtimeSession).toHaveBeenCalledTimes(3)
  const oldConnection = connections.at(-1)!
  await act(async () => render('other-api'))
  expect(oldConnection.stop).toHaveBeenCalled()
  expect(createRealtimeSession).toHaveBeenLastCalledWith('other-api', 'current', expect.any(AbortSignal))
})

it('blocks privileged query and mutation credentials after platform revocation without blocking tenant discovery or tenant reads', async () => {
  const pending = deferred<string>()
  const load = vi.fn<(signal: AbortSignal) => Promise<string>>(async () => 'privileged')
  const discovery = vi.fn(async () => ['tenant'])
  let platform!: ReturnType<typeof useScopedQuery<string>>
  let tenant!: ReturnType<typeof useScopedQuery<string>>
  let tenants!: ReturnType<typeof useScopedQuery<string[]>>
  let privilegedOrigin!: ReturnType<typeof useApiScope>
  let ordinaryOrigin!: ReturnType<typeof useApiScope>
  let tenantOrigin!: ReturnType<typeof useApiScope>
  function Probe() {
    const platformQuery = useScopedQuery({ scope: ['api', undefined], key: ['platform-storage', 'inventory'], privileged: true,
      realtime: [{ topic: 'operations' }], load })
    const tenantQuery = useScopedQuery({ scope: ['api', 'tenant'], key: ['source-builds', 'jobs', 'service'], realtime: interest, load: async () => 'tenant' })
    const tenantsQuery = useScopedQuery({ scope: ['api', undefined], key: ['tenants'], load: discovery })
    const privileged = useApiScope({ baseUrl: 'api', token: 'current', privileged: true })
    const ordinary = useApiScope({ baseUrl: 'api', token: 'current' })
    const scoped = useApiScope({ baseUrl: 'api', tenantId: 'tenant', token: 'current' })
    useLayoutEffect(() => {
      platform = platformQuery; tenant = tenantQuery; tenants = tenantsQuery
      privilegedOrigin = privileged; ordinaryOrigin = ordinary; tenantOrigin = scoped
    })
    return null
  }
  await act(async () => root.render(<RealtimeProvider baseUrl="api" token="current" enabled><Probe /></RealtimeProvider>))
  await act(async () => vi.advanceTimersByTimeAsync(250))
  expect(platform.resource).toBe('privileged')
  expect(tenant.resource).toBe('tenant')
  let signal!: AbortSignal
  load.mockImplementationOnce(value => { signal = value; return pending.promise })
  let refreshing!: Promise<string | null>
  await act(async () => { refreshing = platform.refresh() })
  await act(async () => connections[0].accessRevoked())
  expect(signal.aborted).toBe(true)
  expect(platform.resource).toBeNull()
  expect(privilegedOrigin.isCurrent()).toBe(false)
  expect(() => privilegedOrigin.credential()).toThrow('Aborted')
  expect(ordinaryOrigin.credential()).toBe('current')
  expect(tenantOrigin.credential()).toBe('current')
  expect(tenant.resource).toBe('tenant')
  expect(tenants.resource).toEqual(['tenant'])
  expect(discovery).toHaveBeenCalledTimes(2)
  expect(connections[0].subscribe).toHaveBeenLastCalledWith(interest)
  await act(async () => { pending.resolve('late privileged'); expect(await refreshing).toBeNull() })
  expect(privilegedOrigin.session.client.getQueryData(platform.queryKey)).toBeUndefined()
})

it.each(['postgres', 'redis'] as const)('restores %s reconciliation polling when any required metadata topic loses heartbeat coverage', async (topic) => {
  const interests = resourceInterests(topic, 'tenant', 'resource')
  const load = vi.fn(async () => ({ resourceGroupId: 'group' }))
  function Probe() {
    const query = useScopedQuery({ scope: ['api', 'tenant'], key: [topic, 'detail', 'resource'], realtime: interests, load })
    useVisibleRefresh(true, query.refresh, 2500, interests)
    return null
  }
  await act(async () => root.render(<RealtimeProvider baseUrl="api" token="current" enabled><Probe /></RealtimeProvider>))
  await act(async () => vi.advanceTimersByTimeAsync(250))
  load.mockClear()
  await act(async () => vi.advanceTimersByTimeAsync(9000))
  expect(load).not.toHaveBeenCalled()
  connections[0].heartbeat.mockResolvedValue(interests.map((request, index) => ({ ...request, subscriptionId: String(index), covered: request.topic !== 'workspace-inventory' })))
  await act(async () => vi.advanceTimersByTimeAsync(750))
  await act(async () => vi.advanceTimersByTimeAsync(2500))
  expect(load).toHaveBeenCalledTimes(1)
  await act(async () => { hidden = true; document.dispatchEvent(new Event('visibilitychange')) })
  load.mockClear()
  await act(async () => vi.advanceTimersByTimeAsync(10000))
  expect(load).not.toHaveBeenCalled()
})
