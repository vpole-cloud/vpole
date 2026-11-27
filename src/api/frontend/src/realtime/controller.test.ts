// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

// @vitest-environment jsdom
import { QueryObserver } from '@tanstack/react-query'
import { afterEach, expect, it, vi } from 'vitest'
const { webcrypto } = await vi.importActual<{ webcrypto: Crypto }>('node:crypto')
import { ApiError } from '../api/transport'
import { createQueryClient, scopedQueryKey, type QuerySession } from '../data/querySession'
import { deferred } from '../test/http'
import { RealtimeController } from './controller'
import { FakeConnection } from '../test/fakeRealtimeConnection'
import {
  acceptsChange,
  normalizeChange,
  type RealtimeCoverage,
  type RealtimeSubscription,
} from './types'
import type { RuntimeContext } from './runtimeContext'

const owners: { controller: RealtimeController; session: QuerySession }[] = []
afterEach(() => {
  owners.forEach(({ controller, session }) => {
    controller.dispose()
    session.client.clear()
  })
  owners.length = 0
  vi.useRealTimers()
  vi.unstubAllGlobals()
})
const settle = async () => {
  for (let index = 0; index < 12; index++) await Promise.resolve()
}
function setup(configure?: (connection: FakeConnection) => void) {
  const session: QuerySession = {
    identity: 'account',
    id: 'session',
    client: createQueryClient(),
    isActive: () => true,
  }
  const connections: FakeConnection[] = []
  const bootstrap = vi.fn(async () => ({
    expiresAtUtc: new Date(Date.now() + 300000).toISOString(),
  }))
  const revoke = vi.fn(async () => {})
  const broadcast = vi.fn()
  const controller = new RealtimeController(session, 'api', {
    bootstrap,
    revoke,
    broadcast,
    userId: 'user',
    connection: () => {
      const connection = new FakeConnection()
      configure?.(connection)
      connections.push(connection)
      return connection
    },
  })
  owners.push({ controller, session })
  return { session, controller, connections, bootstrap, revoke, broadcast }
}
const interest: RealtimeSubscription = {
  topic: 'source-builds',
  tenantId: 'alpha',
  resourceId: 'service',
}

it('fences runtime GETs by exact revision/generation and retained version floor while allowing current nullable initial fallback', async () => {
  const { session, controller, connections } = setup()
  const context: RuntimeContext = {
    tenantId: 'alpha',
    deploymentId: 'deployment',
    revision: 7,
    generation: 2,
  }
  const key = scopedQueryKey(session, ['api', 'alpha'], ['health', 'deployment', 7, 2])
  session.client.setQueryData(key, 'snapshot')
  const initial = controller.beginObservationRead('api', context)
  const signal = new AbortController().signal
  expect(controller.acceptObservationRead('api', context, initial, null, signal)).toBe(true)
  controller.register({
    topic: 'runtime-observations',
    tenantId: 'alpha',
    resourceId: 'deployment',
  })
  await controller.renew()
  expect(controller.acceptObservationRead('api', context, initial, 5, signal)).toBe(false)
  connections[0].changed({
    version: 1,
    topic: 'runtime-observations',
    tenantId: 'alpha',
    resourceId: 'deployment',
    revision: 7,
    generation: 2,
    observationVersion: 5,
  })
  const started = controller.beginObservationRead('api', context)
  expect(started.floor).toBe(5)
  expect(controller.acceptObservationRead('api', context, started, 4, signal)).toBe(false)
  expect(controller.acceptObservationRead('api', context, started, null, signal)).toBe(false)
  expect(controller.acceptObservationRead('api', context, started, 5, signal)).toBe(true)
  connections[0].changed({
    version: 1,
    topic: 'runtime-observations',
    tenantId: 'alpha',
    resourceId: 'deployment',
    revision: 7,
    generation: 2,
    observationVersion: 6,
  })
  expect(controller.acceptObservationRead('api', context, started, 6, signal)).toBe(false)
  const next = { ...context, revision: 8, generation: 3 }
  session.client.setQueryData(
    scopedQueryKey(session, ['api', 'alpha'], ['health', 'deployment', 8, 3]),
    'new context',
  )
  const current = controller.beginObservationRead('api', next)
  expect(current.floor).toBe(0)
  expect(controller.acceptObservationRead('api', next, current, null, signal)).toBe(true)
  expect(controller.acceptObservationRead('other-api', next, current, 7, signal)).toBe(false)
})

it('resyncs runtime epochs on coverage/reconnect transitions but keeps healthy heartbeats and duplicate/out-of-order hints unchanged', async () => {
  vi.useFakeTimers()
  const { session, controller, connections } = setup()
  const context: RuntimeContext = {
    tenantId: 'alpha',
    deploymentId: 'deployment',
    revision: 7,
    generation: 2,
  }
  session.client.setQueryData(
    scopedQueryKey(session, ['api', 'alpha'], ['health', 'deployment', 7, 2]),
    'snapshot',
  )
  controller.beginObservationRead('api', context)
  controller.register({
    topic: 'runtime-observations',
    tenantId: 'alpha',
    resourceId: 'deployment',
  })
  await controller.renew()
  const initial = controller.observationReadState('api', context)
  await vi.advanceTimersByTimeAsync(10000)
  expect(controller.observationReadState('api', context)).toEqual(initial)
  const hint = {
    version: 1 as const,
    topic: 'runtime-observations' as const,
    tenantId: 'alpha',
    resourceId: 'deployment',
    revision: 7,
    generation: 2,
    observationVersion: 8,
  }
  connections[0].changed(hint)
  const changed = controller.observationReadState('api', context)
  connections[0].changed(hint)
  connections[0].changed({ ...hint, observationVersion: 7 })
  connections[0].changed({ ...hint, observationVersion: undefined })
  expect(controller.observationReadState('api', context)).toEqual(changed)
  connections[0].reconnecting()
  connections[0].reconnected()
  await settle()
  expect(controller.observationReadState('api', context).epoch).toBeGreaterThan(changed.epoch)
  expect(controller.observationReadState('api', context).floor).toBe(8)
})

it('resyncs uncovered runtime hints and promptly rechecks coverage through one coalesced singleflight heartbeat', async () => {
  vi.useFakeTimers()
  const request: RealtimeSubscription = {
    topic: 'runtime-observations',
    tenantId: 'alpha',
    resourceId: 'deployment',
  }
  const { session, controller, connections } = setup((connection) =>
    connection.subscribe.mockResolvedValue([
      { ...request, subscriptionId: 'runtime', covered: false },
    ]),
  )
  const context: RuntimeContext = {
    tenantId: 'alpha',
    deploymentId: 'deployment',
    revision: 7,
    generation: 2,
  }
  const key = scopedQueryKey(session, ['api', 'alpha'], ['health', 'deployment', 7, 2])
  session.client.setQueryData(key, 'snapshot')
  controller.beginObservationRead('api', context)
  controller.register(request)
  await controller.renew()
  const connection = connections[0]
  const held = deferred<RealtimeCoverage[]>()
  connection.heartbeat
    .mockImplementationOnce(() => held.promise)
    .mockResolvedValue([{ ...request, subscriptionId: 'runtime', covered: true }])
  const hint = {
    version: 1 as const,
    ...request,
    revision: 7,
    generation: 2,
    observationVersion: 1,
  }
  for (let version = 1; version <= 100; version++)
    connection.changed({ ...hint, observationVersion: version })
  expect(controller.covered(request)).toBe(false)
  expect(controller.observationReadState('api', context).floor).toBe(100)
  expect(session.client.getQueryState(key)?.isInvalidated).toBe(true)
  await vi.advanceTimersByTimeAsync(250)
  expect(connection.heartbeat).toHaveBeenCalledTimes(1)
  for (let version = 101; version <= 200; version++)
    connection.changed({ ...hint, observationVersion: version })
  await vi.advanceTimersByTimeAsync(250)
  expect(connection.heartbeat).toHaveBeenCalledTimes(1)
  held.resolve([{ ...request, subscriptionId: 'runtime', covered: false }])
  await settle()
  expect(connection.heartbeat).toHaveBeenCalledTimes(2)
  expect(controller.covered(request)).toBe(true)
  expect(controller.observationReadState('api', context).floor).toBe(200)
  for (let index = 0; index < 100; index++) connection.changed({ ...hint, observationVersion: 199 })
  await vi.advanceTimersByTimeAsync(250)
  expect(connection.heartbeat).toHaveBeenCalledTimes(2)
})

it('cancels expedited coverage checks when a runtime interest leaves or its tenant is revoked', async () => {
  vi.useFakeTimers()
  const { session, controller, connections } = setup()
  const request: RealtimeSubscription = {
    topic: 'runtime-observations',
    tenantId: 'alpha',
    resourceId: 'deployment',
  }
  session.client.setQueryData(
    scopedQueryKey(session, ['api', 'alpha'], ['health', 'deployment', 7, 2]),
    'snapshot',
  )
  const release = controller.register(request)
  await controller.renew()
  const hint = {
    version: 1 as const,
    ...request,
    revision: 7,
    generation: 2,
    observationVersion: 1,
  }
  connections[0].changed(hint)
  release()
  await settle()
  connections[0].changed({ ...hint, observationVersion: 2 })
  await vi.advanceTimersByTimeAsync(250)
  expect(connections[0].heartbeat).not.toHaveBeenCalled()
  controller.register(request)
  await settle()
  connections[0].changed({ ...hint, observationVersion: 2 })
  connections[0].accessRevoked('alpha')
  await settle()
  connections[0].changed({ ...hint, observationVersion: 3 })
  await vi.advanceTimersByTimeAsync(250)
  expect(connections[0].heartbeat).not.toHaveBeenCalled()
})

it('atomically replaces deduplicated subscriptions, resyncs after acknowledgement, and unsubscribes all when hidden owners leave', async () => {
  vi.useFakeTimers()
  const { session, controller, connections } = setup()
  const queryKey = scopedQueryKey(session, ['api', 'alpha'], ['source-builds', 'jobs', 'service'])
  session.client.setQueryData(queryKey, { value: 'old', identity: JSON.stringify(queryKey) })
  const load = vi.fn(async () => ({ value: 'new', identity: JSON.stringify(queryKey) }))
  const observer = new QueryObserver(session.client, {
    queryKey,
    queryFn: load,
    staleTime: Infinity,
  })
  const release = observer.subscribe(() => {})
  const a = controller.register(interest)
  const b = controller.register(interest)
  await controller.renew()
  expect(connections[0].subscribe).toHaveBeenCalledWith([interest])
  expect(controller.covered(interest)).toBe(true)
  await vi.advanceTimersByTimeAsync(250)
  expect(load).toHaveBeenCalledTimes(1)
  a()
  await settle()
  expect(connections[0].unsubscribe).not.toHaveBeenCalled()
  b()
  await settle()
  expect(connections[0].unsubscribe).toHaveBeenCalledTimes(1)
  expect(controller.covered(interest)).toBe(false)
  release()
})

it('ignores late acknowledgements and events from a rotated connection and restores coverage only from a current acknowledgement', async () => {
  const { controller, connections } = setup()
  controller.register(interest)
  const first = controller.renew()
  await settle()
  const old = connections[0]
  await first
  const ack = deferred<RealtimeCoverage[]>()
  old.subscribe.mockImplementation(() => ack.promise)
  old.reconnecting()
  old.reconnected()
  await settle()
  expect(controller.covered(interest)).toBe(false)
  await controller.renew()
  expect(old.stop).toHaveBeenCalled()
  expect(controller.covered(interest)).toBe(true)
  ack.resolve([{ ...interest, subscriptionId: 'old', covered: false }])
  await settle()
  old.sessionRevoked()
  old.accessRevoked('alpha')
  old.close()
  expect(controller.covered(interest)).toBe(true)
  expect(controller.allowed('api', 'alpha')).toBe(true)
})

it('isolates notifications by session, API, tenant and resource without putting payloads into the cache', async () => {
  vi.useFakeTimers()
  const { session, controller, connections } = setup()
  const keys = [
    scopedQueryKey(session, ['api', 'alpha'], ['source-builds', 'jobs', 'service']),
    scopedQueryKey(session, ['api', 'beta'], ['source-builds', 'jobs', 'service']),
    scopedQueryKey(session, ['elsewhere', 'alpha'], ['source-builds', 'jobs', 'service']),
    scopedQueryKey(session, ['api', 'alpha'], ['source-builds', 'jobs', 'other']),
  ]
  keys.forEach((key) =>
    session.client.setQueryData(key, { value: 'saved', identity: JSON.stringify(key) }),
  )
  controller.register(interest)
  await controller.renew()
  keys.forEach((key) =>
    session.client
      .getQueryCache()
      .find({ queryKey: key, exact: true })
      ?.setState({ isInvalidated: false }),
  )
  const connection = connections[0]
  connection.changed({
    version: 1,
    topic: 'source-builds',
    tenantId: 'beta',
    resourceId: 'service',
  })
  expect(keys.map((key) => session.client.getQueryState(key)?.isInvalidated)).toEqual([
    false,
    false,
    false,
    false,
  ])
  connection.changed({
    version: 1,
    topic: 'source-builds',
    tenantId: 'alpha',
    resourceId: 'service',
    revision: 4,
  })
  expect(keys.map((key) => session.client.getQueryState(key)?.isInvalidated)).toEqual([
    true,
    false,
    false,
    false,
  ])
  expect(session.client.getQueryData(keys[0])).toEqual({
    value: 'saved',
    identity: JSON.stringify(keys[0]),
  })
})

it('clears affected tenant metadata, revalidates discovery, blocks revoked sessions across token changes and peer renewal, and deletes before logout returns', async () => {
  const { session, controller, connections, bootstrap, revoke, broadcast } = setup()
  const alpha = scopedQueryKey(session, ['api', 'alpha'], ['web-services', 'detail', 'service'])
  const beta = scopedQueryKey(session, ['api', 'beta'], ['web-services', 'detail', 'service'])
  const tenants = scopedQueryKey(session, ['api', 'user'], ['tenants'])
  session.client.setQueryData(alpha, 'alpha')
  session.client.setQueryData(beta, 'beta')
  session.client.setQueryData(tenants, ['alpha', 'beta'])
  const discovery = vi.fn(async () => ['beta'])
  const observer = new QueryObserver(session.client, {
    queryKey: tenants,
    queryFn: discovery,
    staleTime: Infinity,
  })
  const release = observer.subscribe(() => {})
  controller.register(interest)
  await controller.renew()
  connections[0].accessRevoked('alpha')
  await settle()
  expect(session.client.getQueryData(alpha)).toBeUndefined()
  expect(session.client.getQueryData(beta)).toBe('beta')
  expect(discovery).toHaveBeenCalledTimes(1)
  expect(controller.allowed('api', 'alpha')).toBe(false)
  expect(controller.allowed('api', 'beta')).toBe(true)
  await controller.logout()
  expect(revoke).toHaveBeenCalledTimes(1)
  expect(broadcast).toHaveBeenLastCalledWith({ type: 'session-revoked', tenantId: undefined })
  await controller.renew()
  controller.receive({ type: 'renewed' })
  await settle()
  expect(bootstrap).toHaveBeenCalledTimes(1)
  expect(session.client.getQueryData(beta)).toBeUndefined()
  release()
})

it('revokes platform access locally and across tabs while retaining tenant reads, discovery and subscriptions', async () => {
  const { session, controller, connections, broadcast, bootstrap } = setup()
  const tenant = scopedQueryKey(session, ['api', 'alpha'], ['web-services', 'detail', 'service'])
  const platform = scopedQueryKey(session, ['api', undefined], ['platform-storage', 'inventory'])
  const ordinary = scopedQueryKey(session, ['api', undefined], ['ordinary'])
  const tenants = scopedQueryKey(session, ['api', undefined], ['tenants'])
  session.client.setQueryData(tenant, 'tenant')
  session.client.setQueryData(ordinary, 'ordinary')
  session.client.setQueryData(tenants, ['alpha'])
  session.client
    .getQueryCache()
    .build(session.client, { queryKey: platform, meta: { privileged: true } })
    .setData('privileged')
  const discovery = vi.fn(async () => ['alpha'])
  const observer = new QueryObserver(session.client, {
    queryKey: tenants,
    queryFn: discovery,
    staleTime: Infinity,
  })
  const release = observer.subscribe(() => {})
  const platformInterest: RealtimeSubscription = { topic: 'operations' }
  controller.register(interest)
  controller.register(platformInterest)
  await controller.renew()
  connections[0].accessRevoked()
  await settle()
  expect(session.client.getQueryData(platform)).toBeUndefined()
  expect(session.client.getQueryData(tenant)).toBe('tenant')
  expect(session.client.getQueryData(ordinary)).toBe('ordinary')
  expect(discovery).toHaveBeenCalledTimes(1)
  expect(controller.allowed('api', 'alpha')).toBe(true)
  expect(controller.allowed('api')).toBe(true)
  expect(controller.platformAllowed('api')).toBe(false)
  expect(controller.covered(interest)).toBe(true)
  expect(controller.covered(platformInterest)).toBe(false)
  expect(connections[0].subscribe).toHaveBeenLastCalledWith([interest])
  expect(broadcast).toHaveBeenLastCalledWith({ type: 'access-revoked' })
  await controller.renew()
  expect(bootstrap).toHaveBeenCalledTimes(2)
  expect(connections[1].subscribe).toHaveBeenLastCalledWith([interest])

  const peer = setup()
  const peerPlatform = scopedQueryKey(
    peer.session,
    ['api', undefined],
    ['platform-storage', 'inventory'],
  )
  peer.session.client
    .getQueryCache()
    .build(peer.session.client, { queryKey: peerPlatform, meta: { privileged: true } })
    .setData('privileged')
  peer.controller.register(interest)
  await peer.controller.renew()
  peer.controller.receive({ type: 'access-revoked' })
  await settle()
  expect(peer.session.client.getQueryData(peerPlatform)).toBeUndefined()
  expect(peer.controller.allowed('api', 'alpha')).toBe(true)
  expect(peer.controller.platformAllowed('api')).toBe(false)
  expect(peer.controller.covered(interest)).toBe(true)
  expect(peer.broadcast).not.toHaveBeenCalledWith({ type: 'access-revoked' })
  peer.connections[0].sessionRevoked()
  await settle()
  expect(peer.controller.allowed('api', 'alpha')).toBe(false)
  release()
})

it('falls back when disabled or uncovered and retries infrastructure failures without restoring coverage prematurely', async () => {
  vi.useFakeTimers()
  const { controller, bootstrap, connections } = setup()
  controller.register(interest)
  bootstrap.mockRejectedValueOnce(new ApiError('Disabled', 404))
  await controller.renew()
  await vi.advanceTimersByTimeAsync(20000)
  expect(bootstrap).toHaveBeenCalledTimes(1)
  expect(controller.covered(interest)).toBe(false)
  bootstrap.mockRejectedValueOnce(new ApiError('Unavailable', 503))
  await controller.renew()
  await vi.advanceTimersByTimeAsync(10000)
  expect(bootstrap).toHaveBeenCalledTimes(3)
  connections[0].subscribe.mockResolvedValue([
    { ...interest, subscriptionId: 'uncovered', covered: false },
  ])
  connections[0].reconnecting()
  connections[0].reconnected()
  await settle()
  expect(controller.status).toBe('connected')
  expect(controller.covered(interest)).toBe(false)
})

it('normalizes nullable wire scopes before matching platform query envelopes', async () => {
  const { session, controller, connections } = setup()
  const queryKey = scopedQueryKey(session, ['api', undefined], ['operations', null, null, null, 1])
  session.client.setQueryData(queryKey, { value: 'saved', identity: JSON.stringify(queryKey) })
  controller.register({ topic: 'operations' })
  await controller.renew()
  session.client.getQueryCache().find({ queryKey, exact: true })?.setState({ isInvalidated: false })
  const change = normalizeChange({
    version: 1,
    topic: 'operations',
    tenantId: null,
    resourceId: null,
    resourceGroupId: null,
    generation: null,
  })
  expect(change.tenantId).toBeUndefined()
  connections[0].changed(change)
  expect(session.client.getQueryState(queryKey)?.isInvalidated).toBe(true)
})

it('does not restart a disposed connection when failure cleanup completes late', async () => {
  vi.useFakeTimers()
  const stopped = deferred<void>()
  const { controller, bootstrap } = setup((connection) => {
    connection.start.mockRejectedValue(new Error('Transport failed'))
    connection.stop.mockImplementation(() => stopped.promise)
  })
  const connecting = controller.renew()
  await settle()
  controller.dispose()
  stopped.resolve()
  await connecting
  await vi.advanceTimersByTimeAsync(20000)
  expect(bootstrap).toHaveBeenCalledTimes(1)
})

it('leaves bearer reads available but blocks cookie takeover after an account mismatch', async () => {
  vi.useFakeTimers()
  const { controller, bootstrap } = setup()
  bootstrap.mockRejectedValueOnce(new ApiError('Another account owns the browser cookie', 409))
  await controller.renew()
  await vi.advanceTimersByTimeAsync(20000)
  controller.updateCredential('rotated')
  await controller.renew()
  controller.receive({ type: 'renewed' })
  await settle()
  expect(bootstrap).toHaveBeenCalledTimes(1)
  expect(controller.allowed('api', 'tenant')).toBe(true)
  expect(controller.status).toBe('offline')
})

it('fences queued old-scope resync on interest replacement without cancelling an ongoing HTTP read', async () => {
  vi.useFakeTimers()
  const { session, controller } = setup()
  const pending = deferred<string>()
  let signal!: AbortSignal
  const key = scopedQueryKey(session, ['api', 'alpha'], ['source-builds', 'jobs', 'service'])
  const load = vi.fn(({ signal: value }: { signal: AbortSignal }) => {
    signal = value
    return pending.promise
  })
  const observer = new QueryObserver(session.client, { queryKey: key, queryFn: load })
  const releaseRead = observer.subscribe(() => {})
  const releaseInterest = controller.register(interest)
  await controller.renew()
  releaseInterest()
  await settle()
  await vi.advanceTimersByTimeAsync(250)
  expect(signal.aborted).toBe(false)
  pending.resolve('current')
  await settle()
  expect(load).toHaveBeenCalledTimes(1)
  releaseRead()
})

it('subscribes the current scope even when a replaced in-flight subscription fails', async () => {
  const pending = deferred<RealtimeCoverage[]>()
  const { controller, connections } = setup((connection) =>
    connection.subscribe.mockImplementationOnce(() => pending.promise),
  )
  const release = controller.register(interest)
  const connecting = controller.renew()
  await settle()
  release()
  controller.register({ ...interest, tenantId: 'beta' })
  await settle()
  pending.reject(new Error('Old membership unavailable'))
  await connecting
  await settle()
  expect(connections[0].subscribe).toHaveBeenLastCalledWith([{ ...interest, tenantId: 'beta' }])
  expect(controller.covered({ ...interest, tenantId: 'beta' })).toBe(true)
  expect(controller.covered(interest)).toBe(false)
})

it('waits for session deletion before logout completes and still finishes if deletion is unavailable', async () => {
  const deleted = deferred<void>()
  const { controller, revoke } = setup()
  revoke.mockImplementation(() => deleted.promise)
  const completed = vi.fn()
  const loggingOut = controller.logout().then(completed)
  await settle()
  expect(revoke).toHaveBeenCalledTimes(1)
  expect(completed).not.toHaveBeenCalled()
  deleted.reject(new Error('API offline'))
  await loggingOut
  expect(completed).toHaveBeenCalledTimes(1)
  expect(controller.allowed('api', 'tenant')).toBe(false)
})

it('drops coverage and restores polling when a live WebSocket heartbeat detects an infrastructure outage', async () => {
  vi.useFakeTimers()
  const { controller, bootstrap, connections } = setup()
  controller.register(interest)
  await controller.renew()
  connections[0].heartbeat.mockRejectedValue(new Error('Redis unavailable'))
  bootstrap.mockRejectedValueOnce(new ApiError('Unavailable', 503))
  await vi.advanceTimersByTimeAsync(10000)
  expect(connections[0].stop).toHaveBeenCalled()
  expect(controller.covered(interest)).toBe(false)
  expect(controller.status).toBe('offline')
  expect(controller.allowed('api', 'alpha')).toBe(true)
  await vi.advanceTimersByTimeAsync(10000)
  expect(controller.covered(interest)).toBe(true)
})

it('never restores removed interests from a late heartbeat acknowledgement', async () => {
  vi.useFakeTimers()
  const { controller, connections } = setup()
  const release = controller.register(interest)
  await controller.renew()
  const pending = deferred<RealtimeCoverage[]>()
  connections[0].heartbeat.mockImplementation(() => pending.promise)
  await vi.advanceTimersByTimeAsync(10000)
  release()
  controller.register({ ...interest, tenantId: 'beta' })
  await settle()
  pending.resolve([{ ...interest, subscriptionId: 'old', covered: true }])
  await settle()
  expect(controller.covered(interest)).toBe(false)
  expect(controller.covered({ ...interest, tenantId: 'beta' })).toBe(true)
})

it('keeps healthy heartbeats from causing repeated HTTP resyncs and stops them when offline', async () => {
  vi.useFakeTimers()
  const { session, controller, connections } = setup()
  const key = scopedQueryKey(session, ['api', 'alpha'], ['source-builds', 'jobs', 'service'])
  session.client.setQueryData(key, 'saved')
  const load = vi.fn(async () => 'fresh')
  const observer = new QueryObserver(session.client, {
    queryKey: key,
    queryFn: load,
    staleTime: Infinity,
  })
  const release = observer.subscribe(() => {})
  controller.register(interest)
  await controller.renew()
  await vi.advanceTimersByTimeAsync(250)
  load.mockClear()
  await vi.advanceTimersByTimeAsync(30000)
  expect(connections[0].heartbeat).toHaveBeenCalledTimes(3)
  expect(load).not.toHaveBeenCalled()
  controller.dispose()
  await vi.advanceTimersByTimeAsync(30000)
  expect(connections[0].heartbeat).toHaveBeenCalledTimes(3)
  release()
})

it('matches own-user discovery by its account key, rejects wildcard subjects and retains user coverage after platform denial', async () => {
  vi.useFakeTimers()
  const { session, controller, connections } = setup()
  const own: RealtimeSubscription = { topic: 'tenant-access', resourceId: 'user' }
  const keys = [
    scopedQueryKey(session, ['api', 'user'], ['tenants']),
    scopedQueryKey(session, ['api', 'other'], ['tenants']),
    scopedQueryKey(session, ['other-api', 'user'], ['tenants']),
    scopedQueryKey(session, ['api', 'user'], ['ordinary']),
  ]
  keys.forEach((key) => session.client.setQueryData(key, 'saved'))
  controller.register(own)
  controller.register({ topic: 'tenant-access', resourceId: 'other' })
  controller.register({ topic: 'tenant-access' })
  await controller.renew()
  expect(connections[0].subscribe).toHaveBeenCalledWith([own])
  expect(acceptsChange(own, { version: 1, topic: 'tenant-access' })).toBe(false)
  expect(acceptsChange(own, { version: 1, topic: 'tenant-access', resourceId: 'other' })).toBe(
    false,
  )
  keys.forEach((key) =>
    session.client
      .getQueryCache()
      .find({ queryKey: key, exact: true })
      ?.setState({ isInvalidated: false }),
  )
  connections[0].changed({ version: 1, topic: 'tenant-access', resourceId: 'user' })
  expect(keys.map((key) => session.client.getQueryState(key)?.isInvalidated)).toEqual([
    true,
    false,
    false,
    false,
  ])
  connections[0].accessRevoked()
  await settle()
  expect(connections[0].subscribe).toHaveBeenLastCalledWith([own])
  expect(controller.covered(own)).toBe(true)
  expect(controller.platformAllowed('api')).toBe(false)
})

it('accepts only current successful membership reads, removes tenant caches and restores membership without restoring a revoked session', async () => {
  const { session, controller, connections } = setup()
  const alpha = scopedQueryKey(session, ['api', 'alpha'], ['web-services', 'detail', 'service'])
  const beta = scopedQueryKey(session, ['api', 'beta'], ['web-services', 'detail', 'other'])
  const signal = new AbortController().signal
  controller.register({ topic: 'tenant-access', resourceId: 'user' })
  await controller.renew()
  const started = () => controller.beginMembershipRead('api', 'user')
  expect(
    controller.acceptMembershipSnapshot('api', 'user', started(), ['alpha', 'beta'], signal),
  ).toBe(true)
  session.client.setQueryData(alpha, 'alpha')
  session.client.setQueryData(beta, 'beta')
  const old = started()
  connections[0].accessRevoked('alpha')
  await settle()
  expect(controller.acceptMembershipSnapshot('api', 'user', old, ['alpha', 'beta'], signal)).toBe(
    false,
  )
  expect(controller.allowed('api', 'alpha')).toBe(false)
  const hinted = started()
  connections[0].changed({ version: 1, topic: 'tenant-access', resourceId: 'user' })
  expect(
    controller.acceptMembershipSnapshot('api', 'user', hinted, ['alpha', 'beta'], signal),
  ).toBe(false)
  expect(
    controller.acceptMembershipSnapshot('api', 'user', started(), ['alpha', 'beta'], signal),
  ).toBe(true)
  expect(controller.allowed('api', 'alpha')).toBe(true)
  session.client.setQueryData(alpha, 'restored')
  expect(controller.acceptMembershipSnapshot('api', 'user', started(), ['beta'], signal)).toBe(true)
  expect(session.client.getQueryData(alpha)).toBeUndefined()
  expect(session.client.getQueryData(beta)).toBe('beta')
  expect(controller.allowed('api', 'alpha')).toBe(false)
  expect(
    controller.acceptMembershipSnapshot('other-api', 'user', started(), ['alpha'], signal),
  ).toBe(false)
  const canceled = new AbortController()
  canceled.abort()
  expect(
    controller.acceptMembershipSnapshot('api', 'user', started(), ['alpha'], canceled.signal),
  ).toBe(false)
  const revoked = started()
  connections[0].sessionRevoked()
  expect(
    controller.acceptMembershipSnapshot('api', 'user', revoked, ['alpha', 'beta'], signal),
  ).toBe(false)
  expect(controller.allowed('api', 'beta')).toBe(false)
})

it('reserves acknowledged own-user discovery before the 64-interest limit and leaves excluded resource interests uncovered', async () => {
  const { controller, connections } = setup()
  const resources: RealtimeSubscription[] = Array.from({ length: 70 }, (_, index) => ({
    topic: 'resource-groups',
    tenantId: 'alpha',
    resourceId: `group-${index}`,
  }))
  resources.forEach((request) => controller.register(request))
  const own: RealtimeSubscription = { topic: 'tenant-access', resourceId: 'user' }
  controller.register({ topic: 'tenant-access', resourceId: 'other' })
  controller.register(own)
  await controller.renew()
  expect(connections[0].subscribe).toHaveBeenCalledWith([own, ...resources.slice(0, 63)])
  expect(controller.covered(own)).toBe(true)
  expect(controller.covered(resources[62])).toBe(true)
  expect(controller.covered(resources[63])).toBe(false)
  expect(controller.covered(resources[69])).toBe(false)
})

it('restores platform authority only after a newer current admin bearer bootstrap while retaining user and tenant ownership', async () => {
  vi.stubGlobal('crypto', webcrypto)
  vi.useFakeTimers()
  const { controller, connections } = setup()
  const own: RealtimeSubscription = { topic: 'tenant-access', resourceId: 'user' }
  const platform: RealtimeSubscription = { topic: 'storage-profiles' }
  controller.register(own)
  controller.register(interest)
  controller.register(platform)
  controller.updateCredential('first', true)
  await controller.renew()
  connections[0].accessRevoked()
  await settle()
  expect(controller.platformAllowed('api')).toBe(false)
  expect(controller.covered(own)).toBe(true)
  expect(controller.covered(interest)).toBe(true)
  connections[0].heartbeat.mockResolvedValue(
    [own, interest, platform].map((request, index) => ({
      ...request,
      subscriptionId: String(index),
      covered: true,
    })),
  )
  await vi.advanceTimersByTimeAsync(10000)
  expect(controller.platformAllowed('api')).toBe(false)
  expect(controller.covered(platform)).toBe(false)
  await controller.renew()
  controller.receive({ type: 'renewed' })
  await settle()
  expect(controller.platformAllowed('api')).toBe(false)
  controller.updateCredential('non-admin', false)
  await controller.renew()
  expect(controller.platformAllowed('api')).toBe(false)
  controller.updateCredential('fresh-admin', true)
  await controller.renew()
  expect(controller.platformAllowed('api')).toBe(true)
  expect(controller.covered(platform)).toBe(true)
  expect(controller.covered(own)).toBe(true)
  expect(controller.allowed('api', 'alpha')).toBe(true)
  // Replaying a credential that previously received denial cannot become a fresh grant.
  connections.at(-1)!.accessRevoked()
  await settle()
  controller.updateCredential('first', true)
  await controller.renew()
  expect(controller.platformAllowed('api')).toBe(false)
})

it('synchronously fences claim loss before replacing the old admin fingerprint and rejects its later ABA replay', async () => {
  vi.stubGlobal('crypto', webcrypto)
  const { session, controller, broadcast } = setup()
  const own: RealtimeSubscription = { topic: 'tenant-access', resourceId: 'user' }
  const platform: RealtimeSubscription = { topic: 'storage-profiles' }
  const privateKey = scopedQueryKey(session, ['api', undefined], ['platform-storage', 'inventory'])
  const tenantKey = scopedQueryKey(session, ['api', 'alpha'], ['web-services', 'detail', 'service'])
  const discoveryKey = scopedQueryKey(session, ['api', 'user'], ['tenants'])
  session.client.setQueryData(tenantKey, 'tenant metadata')
  session.client.setQueryData(discoveryKey, ['alpha'])
  const held = deferred<string>()
  let signal!: AbortSignal
  const observer = new QueryObserver(session.client, {
    queryKey: privateKey,
    meta: { privileged: true },
    initialData: 'old private metadata',
    staleTime: Infinity,
    queryFn: ({ signal: current }) => {
      signal = current
      return held.promise
    },
  })
  const release = observer.subscribe(() => {})
  controller.register(own)
  controller.register(interest)
  controller.register(platform)
  controller.updateCredential('old-admin', true)
  await controller.renew()
  const read = observer.refetch({ cancelRefetch: false })
  // No server event: the incoming credential's role claim is sufficient to revoke.
  controller.updateCredential('new-readonly', false)
  expect(controller.platformAllowed('api')).toBe(false)
  expect(signal.aborted).toBe(true)
  expect(session.client.getQueryData(privateKey)).toBeUndefined()
  expect(session.client.getQueryData(tenantKey)).toBe('tenant metadata')
  expect(session.client.getQueryData(discoveryKey)).toEqual(['alpha'])
  expect(controller.allowed('api', 'alpha')).toBe(true)
  expect(broadcast).toHaveBeenLastCalledWith({ type: 'access-revoked' })
  await controller.renew()
  expect(controller.covered(own)).toBe(true)
  expect(controller.covered(interest)).toBe(true)
  expect(controller.covered(platform)).toBe(false)
  held.resolve('late private metadata')
  await read
  expect(session.client.getQueryData(privateKey)).toBeUndefined()
  controller.updateCredential('old-admin', true)
  await controller.renew()
  expect(controller.platformAllowed('api')).toBe(false)
  controller.updateCredential('genuinely-new-admin', true)
  await controller.renew()
  expect(controller.platformAllowed('api')).toBe(true)
  expect(controller.covered(own)).toBe(true)
  expect(controller.covered(interest)).toBe(true)
  release()
})

it('fences an admin bootstrap overtaken by a later denial or credential update', async () => {
  vi.stubGlobal('crypto', webcrypto)
  const { controller, bootstrap } = setup()
  controller.updateCredential('first', true)
  await controller.renew()
  controller.receive({ type: 'access-revoked' })
  const denied = deferred<{ expiresAtUtc: string }>()
  bootstrap.mockReturnValueOnce(denied.promise)
  controller.updateCredential('second', true)
  const deniedRenewal = controller.renew()
  await settle()
  controller.receive({ type: 'access-revoked' })
  denied.resolve({ expiresAtUtc: new Date(Date.now() + 300000).toISOString() })
  await deniedRenewal
  expect(controller.platformAllowed('api')).toBe(false)
  const old = deferred<{ expiresAtUtc: string }>()
  bootstrap.mockReturnValueOnce(old.promise)
  controller.updateCredential('third', true)
  const oldRenewal = controller.renew()
  await settle()
  controller.updateCredential('fourth', true)
  old.resolve({ expiresAtUtc: new Date(Date.now() + 300000).toISOString() })
  await oldRenewal
  expect(controller.platformAllowed('api')).toBe(false)
  await controller.renew()
  expect(controller.platformAllowed('api')).toBe(true)
})

it('bounds denied fingerprint history and waits out an evicted unexpired identity before recovery', async () => {
  vi.stubGlobal('crypto', webcrypto)
  vi.useFakeTimers()
  const { controller } = setup()
  const horizon = Date.now() + 1000
  const token = (id: string, expiresAt: number) =>
    `header.${btoa(JSON.stringify({ jti: id, exp: expiresAt / 1000 }))}.signature`
  for (let index = 0; index < 65; index++) {
    controller.updateCredential(token(String(index), horizon), true)
    controller.receive({ type: 'access-revoked' })
  }
  await controller.renew()
  controller.updateCredential(token('fresh', Date.now() + 300000), true)
  await controller.renew()
  expect(controller.platformAllowed('api')).toBe(false)
  await vi.advanceTimersByTimeAsync(1001)
  await controller.renew()
  expect(controller.platformAllowed('api')).toBe(true)
})

it('keeps platform recovery denied without native fingerprint support while retaining tenant access', async () => {
  vi.stubGlobal('crypto', { subtle: undefined })
  const { controller } = setup()
  controller.updateCredential('first', true)
  await controller.renew()
  controller.receive({ type: 'access-revoked' })
  controller.updateCredential('second', true)
  await controller.renew()
  expect(controller.platformAllowed('api')).toBe(false)
  expect(controller.allowed('api', 'alpha')).toBe(true)
})

it('isolates platform storage floors by exact target/connection and restores coverage only from its coalesced acknowledgement', async () => {
  vi.useFakeTimers()
  const request: RealtimeSubscription = { topic: 'storage-observations', resourceId: 'target' }
  const { session, controller, connections } = setup((connection) =>
    connection.subscribe.mockResolvedValue([
      { ...request, subscriptionId: 'storage', covered: false },
    ]),
  )
  const context = { targetId: 'target', connectionVersion: 7 }
  const key = scopedQueryKey(
    session,
    ['api', undefined],
    ['platform-storage', 'discovery', 'target', 7],
  )
  session.client.setQueryData(key, 'snapshot')
  controller.beginObservationRead('api', context)
  controller.register(request)
  await controller.renew()
  const initial = controller.beginObservationRead('api', context)
  const signal = new AbortController().signal
  connections[0].changed({ version: 1, ...request, revision: 6, observationVersion: 100 })
  connections[0].changed({
    version: 1,
    ...request,
    tenantId: 'alpha',
    revision: 7,
    observationVersion: 101,
  })
  connections[0].changed({ version: 1, ...request, revision: 7, observationVersion: undefined })
  expect(controller.observationReadState('api', context)).toEqual(initial)
  const held = deferred<RealtimeCoverage[]>()
  connections[0].heartbeat
    .mockImplementationOnce(() => held.promise)
    .mockResolvedValue([{ ...request, subscriptionId: 'storage', covered: true }])
  for (let version = 1; version <= 20; version++)
    connections[0].changed({ version: 1, ...request, revision: 7, observationVersion: version })
  expect(controller.acceptObservationRead('api', context, initial, 20, signal)).toBe(false)
  expect(controller.covered(request)).toBe(false)
  await vi.advanceTimersByTimeAsync(250)
  expect(connections[0].heartbeat).toHaveBeenCalledTimes(1)
  connections[0].changed({ version: 1, ...request, revision: 7, observationVersion: 21 })
  await vi.advanceTimersByTimeAsync(250)
  expect(connections[0].heartbeat).toHaveBeenCalledTimes(1)
  held.resolve([{ ...request, subscriptionId: 'storage', covered: false }])
  await settle()
  expect(connections[0].heartbeat).toHaveBeenCalledTimes(2)
  expect(controller.covered(request)).toBe(true)
  const current = controller.beginObservationRead('api', context)
  expect(controller.acceptObservationRead('api', context, current, null, signal)).toBe(false)
  expect(controller.acceptObservationRead('api', context, current, 21, signal)).toBe(true)
  const floor = current.floor
  connections[0].changed({ version: 1, ...request, revision: 7, observationVersion: 21 })
  connections[0].changed({ version: 1, ...request, revision: 7, observationVersion: 20 })
  expect(controller.observationReadState('api', context)).toEqual(current)
  connections[0].accessRevoked()
  expect(controller.acceptObservationRead('api', context, current, floor, signal)).toBe(false)
})
