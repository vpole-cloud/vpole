// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

// @vitest-environment jsdom
import { act, useLayoutEffect, useState } from 'react'
import type { Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { fetchTenants } from '../api'
import { useApiScope } from '../data/useApiScope'
import { scopedQueryKey, useQuerySession, type QuerySession } from '../data/querySession'
import { createRealtimeConnection } from '../realtime/connection'
import { useRealtime } from '../realtime/context'
import type { RealtimeController } from '../realtime/controller'
import { RealtimeProvider } from '../realtime/RealtimeProvider'
import { createRealtimeSession } from '../realtime/sessionApi'
import type { RealtimeCoverage } from '../realtime/types'
import { FakeConnection } from '../test/fakeRealtimeConnection'
import { deferred } from '../test/http'
import { createTestApp } from '../test/TestApp'
import type { Tenant } from '../types'
import { useTenants } from './useTenants'

vi.mock('../api', () => ({ fetchTenants: vi.fn() }))
vi.mock('../realtime/connection', () => ({ createRealtimeConnection: vi.fn() }))
vi.mock('../realtime/sessionApi', () => ({
  createRealtimeSession: vi.fn(),
  revokeRealtimeSession: vi.fn(async () => {}),
}))
const alpha: Tenant = { id: 'alpha', name: 'Alpha', role: 'Member', isDefault: true }
const beta: Tenant = { id: 'beta', name: 'Beta', role: 'Viewer', isDefault: false }
let root: Root
let connection: FakeConnection
let state!: ReturnType<typeof useTenants>
let session!: QuerySession
let origin!: ReturnType<typeof useApiScope>
let controller!: RealtimeController
let draft = ''
let edit!: (value: string) => void
let hidden = false
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  vi.useFakeTimers()
  vi.clearAllMocks()
  localStorage.clear()
  hidden = false
  Object.defineProperty(document, 'hidden', { configurable: true, get: () => hidden })
  window.dispatchEvent(new Event('pageshow'))
  root = createTestApp().root
  connection = new FakeConnection()
  vi.mocked(createRealtimeConnection).mockReturnValue(connection)
  vi.mocked(createRealtimeSession).mockResolvedValue({
    expiresAtUtc: new Date(Date.now() + 300000).toISOString(),
  })
  vi.mocked(fetchTenants).mockResolvedValue([alpha, beta])
})
afterEach(async () => {
  await act(async () => root.unmount())
  window.dispatchEvent(new Event('pageshow'))
  vi.useRealTimers()
})
function Probe({ token, userId = 'user' }: { token: string; userId?: string }) {
  const value = useTenants('api', token, true, userId)
  const querySession = useQuerySession()
  const api = useApiScope({ baseUrl: 'api', token, tenantId: 'alpha' })
  const realtime = useRealtime()
  const [text, setText] = useState('saved')
  useLayoutEffect(() => {
    state = value
    session = querySession
    origin = api
    controller = realtime!
    draft = text
    edit = setText
  }, [value, querySession, api, realtime, text])
  return null
}
const render = (token = 'current', enabled = true, userId = 'user') =>
  root.render(
    <RealtimeProvider baseUrl="api" token={token} enabled={enabled} userId={userId}>
      <Probe token={token} userId={userId} />
    </RealtimeProvider>,
  )

it('does not issue discovery reads or a wildcard user interest when the authenticated subject is absent', async () => {
  await act(async () => render('current', true, ''))
  await act(async () => vi.advanceTimersByTimeAsync(60000))
  expect(fetchTenants).not.toHaveBeenCalled()
  expect(connection.subscribe).not.toHaveBeenCalled()
  expect(state.items).toEqual([])
})

it('fences an old discovery response when its own hint arrives before the subscription acknowledgement', async () => {
  const old = deferred<Tenant[]>()
  const ack = deferred<RealtimeCoverage[]>()
  let signal!: AbortSignal
  vi.mocked(fetchTenants).mockImplementationOnce((_base, _token, value) => {
    signal = value!
    return old.promise
  })
  vi.mocked(fetchTenants).mockResolvedValue([{ ...alpha, role: 'Viewer' }])
  connection.subscribe.mockImplementationOnce(() => ack.promise)
  await act(async () => render())
  await act(async () =>
    connection.changed({ version: 1, topic: 'tenant-access', resourceId: 'user' }),
  )
  expect(signal.aborted).toBe(false)
  await act(async () => old.resolve([alpha]))
  expect(state.items).toEqual([])
  expect(
    session.client.getQueryData(scopedQueryKey(session, ['api', 'user'], ['tenants'])),
  ).toBeUndefined()
  await act(async () =>
    ack.resolve([
      { topic: 'tenant-access', resourceId: 'user', subscriptionId: 'own', covered: true },
    ]),
  )
  await act(async () => vi.advanceTimersByTimeAsync(250))
  expect(state.activeTenant?.role).toBe('Viewer')
  expect(fetchTenants).toHaveBeenCalledTimes(2)
})

it('removes affected metadata, rejects a withheld pre-denial response, and restores only an authoritative current membership without replacing drafts', async () => {
  await act(async () => render())
  await act(async () => vi.advanceTimersByTimeAsync(250))
  const alphaKey = scopedQueryKey(session, ['api', 'alpha'], ['web-services', 'detail', 'service'])
  const betaKey = scopedQueryKey(session, ['api', 'beta'], ['redis', 'detail', 'other'])
  session.client.setQueryData(alphaKey, 'alpha')
  session.client.setQueryData(betaKey, 'beta')
  await act(async () => edit('unsaved'))
  const old = deferred<Tenant[]>()
  vi.mocked(fetchTenants).mockImplementationOnce(() => old.promise)
  const ownKey = scopedQueryKey(session, ['api', 'user'], ['tenants'])
  let reading!: Promise<void>
  await act(async () => {
    reading = session.client.refetchQueries(
      { queryKey: ownKey, exact: true },
      { cancelRefetch: false },
    )
  })
  vi.mocked(fetchTenants).mockResolvedValue([beta])
  await act(async () => connection.accessRevoked('alpha'))
  expect(origin.isCurrent()).toBe(false)
  expect(state.items).toEqual([beta])
  expect(session.client.getQueryData(alphaKey)).toBeUndefined()
  expect(session.client.getQueryData(betaKey)).toBe('beta')
  await act(async () => {
    old.resolve([alpha, beta])
    await reading
  })
  expect(controller.allowed('api', 'alpha')).toBe(false)
  expect(state.items).toEqual([beta])
  vi.mocked(fetchTenants).mockResolvedValue([{ ...alpha, role: 'Viewer' }, beta])
  await act(async () =>
    connection.changed({ version: 1, topic: 'tenant-access', resourceId: 'user' }),
  )
  await act(async () => vi.advanceTimersByTimeAsync(500))
  expect(origin.isCurrent()).toBe(true)
  expect(state.activeTenant?.role).toBe('Viewer')
  await act(async () => render('rotated'))
  expect(draft).toBe('unsaved')
  expect(controller.allowed('api', 'alpha')).toBe(true)
  await act(async () => connection.accessRevoked())
  expect(connection.subscribe).toHaveBeenLastCalledWith([
    { topic: 'tenant-access', resourceId: 'user', tenantId: undefined },
  ])
  expect(state.items).toHaveLength(2)
})

it('uses sixty-second discovery fallback when realtime is disabled and makes no hidden GETs', async () => {
  await act(async () => render('current', false))
  const initial = vi.mocked(fetchTenants).mock.calls.length
  await act(async () => vi.advanceTimersByTimeAsync(60000))
  expect(fetchTenants).toHaveBeenCalledTimes(initial + 1)
  await act(async () => {
    hidden = true
    document.dispatchEvent(new Event('visibilitychange'))
  })
  await act(async () => vi.advanceTimersByTimeAsync(120000))
  expect(fetchTenants).toHaveBeenCalledTimes(initial + 1)
  await act(async () => {
    hidden = false
    document.dispatchEvent(new Event('visibilitychange'))
  })
  expect(fetchTenants).toHaveBeenCalledTimes(initial + 2)
})
