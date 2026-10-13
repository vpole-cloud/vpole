// @vitest-environment jsdom
import { QueryObserver } from '@tanstack/react-query'
import { afterEach, expect, it, vi } from 'vitest'
import { ApiError } from '../api/transport'
import { createQueryClient, scopedQueryKey, type QuerySession } from '../data/querySession'
import { deferred } from '../test/http'
import { FakeConnection } from '../test/fakeRealtimeConnection'
import { RealtimeController } from './controller'
import type { RealtimeCoverage, RealtimeSubscription } from './types'

const controllers: RealtimeController[] = []
afterEach(() => { controllers.forEach(controller => controller.dispose()); controllers.length = 0; vi.useRealTimers() })
const settle = async () => { for (let index = 0; index < 16; index++) await Promise.resolve() }
const request: RealtimeSubscription = { topic: 'runtime-observations', tenantId: 'alpha', resourceId: 'deployment' }
const coverage = (covered: boolean): RealtimeCoverage[] => [{ ...request, subscriptionId: 'owned', covered }]
function setup(configure?: (connection: FakeConnection) => void, id = 'session') {
  let active = true
  const session: QuerySession = { identity: 'account', id, client: createQueryClient(), isActive: () => active }
  const connections: FakeConnection[] = []
  const bootstrap = vi.fn(async () => ({ expiresAtUtc: new Date(Date.now() + 300000).toISOString() }))
  const controller = new RealtimeController(session, 'api', { bootstrap, revoke: async () => {},
    connection: () => { const connection = new FakeConnection(); configure?.(connection); connections.push(connection); return connection } })
  controllers.push(controller)
  return { controller, connections, bootstrap, session, deactivate: () => { active = false } }
}
const reconnect = async (connection: FakeConnection) => { connection.reconnecting(); connection.reconnected(); await settle() }

it('reports no initial uncovered or ordinary replacement fallback, and healthy heartbeats neither report nor refetch', async () => {
  vi.useFakeTimers()
  const { controller, connections, session } = setup(connection => connection.subscribe.mockResolvedValue(coverage(false)))
  const key = scopedQueryKey(session, ['api', 'alpha'], ['health', 'deployment', 7, 2])
  session.client.setQueryData(key, 'current')
  const load = vi.fn(async () => 'current')
  const observer = new QueryObserver(session.client, { queryKey: key, queryFn: load, staleTime: Infinity })
  const releaseQuery = observer.subscribe(() => {})
  const release = controller.register(request); await controller.renew()
  const connection = connections[0]
  connection.heartbeat.mockResolvedValue(coverage(false))
  await vi.advanceTimersByTimeAsync(30000)
  expect(connection.reportLifecycle).not.toHaveBeenCalled()
  connection.subscribe.mockResolvedValue(coverage(true)); release(); controller.register(request); await settle()
  await vi.advanceTimersByTimeAsync(250); load.mockClear()
  connection.heartbeat.mockResolvedValue(coverage(true))
  await vi.advanceTimersByTimeAsync(30000)
  expect(connection.reportLifecycle).not.toHaveBeenCalled()
  expect(load).not.toHaveBeenCalled()
  expect(controller.covered(request)).toBe(true)
  releaseQuery(); session.client.clear()
})

it('counts successful automatic and controlled credential reconnects after the first connection and throttles across replacements', async () => {
  vi.useFakeTimers()
  const { controller, connections } = setup()
  controller.register(request); await controller.renew()
  expect(connections[0].reportLifecycle).not.toHaveBeenCalled()
  await reconnect(connections[0])
  expect(connections[0].reportLifecycle).toHaveBeenCalledExactlyOnceWith({ reconnects: 1, fallbacks: 1 })
  controller.updateCredential('renewed'); await controller.renew()
  expect(connections[1].reportLifecycle).not.toHaveBeenCalled()
  await vi.advanceTimersByTimeAsync(10000)
  expect(connections[1].reportLifecycle).toHaveBeenCalledExactlyOnceWith({ reconnects: 1, fallbacks: 1 })
  await vi.advanceTimersByTimeAsync(10000)
  expect(connections[1].reportLifecycle).toHaveBeenCalledTimes(1)
})

it('retains false and failed reports until healthy heartbeats without changing authority, coverage or bootstrap', async () => {
  vi.useFakeTimers()
  const { controller, connections, bootstrap } = setup(connection => connection.reportLifecycle.mockResolvedValueOnce(false).mockRejectedValueOnce(new Error('Older server')).mockResolvedValue(true))
  controller.register(request); await controller.renew()
  const connection = connections[0]
  await reconnect(connection)
  await vi.advanceTimersByTimeAsync(10000)
  await vi.advanceTimersByTimeAsync(10000)
  expect(connection.reportLifecycle.mock.calls.map(([report]) => report)).toEqual(Array(3).fill({ reconnects: 1, fallbacks: 1 }))
  expect(bootstrap).toHaveBeenCalledTimes(1)
  expect(controller.allowed('api', 'alpha')).toBe(true)
  expect(controller.covered(request)).toBe(true)
  await vi.advanceTimersByTimeAsync(10000)
  expect(connection.reportLifecycle).toHaveBeenCalledTimes(3)
})

it('saturates a burst at two integer counts and reports only once per ten seconds', async () => {
  vi.useFakeTimers()
  const { controller, connections } = setup(connection => connection.reportLifecycle.mockResolvedValue(false))
  controller.register(request); await controller.renew()
  const connection = connections[0]
  for (let index = 0; index < 205; index++) await reconnect(connection)
  expect(connection.reportLifecycle).toHaveBeenCalledTimes(1)
  connection.reportLifecycle.mockResolvedValue(true)
  await vi.advanceTimersByTimeAsync(10000)
  expect(connection.reportLifecycle).toHaveBeenLastCalledWith({ reconnects: 100, fallbacks: 100 })
  expect(connection.reportLifecycle).toHaveBeenCalledTimes(2)
  await vi.advanceTimersByTimeAsync(10000)
  expect(connection.reportLifecycle).toHaveBeenCalledTimes(2)
})

it('counts one actual acknowledgement loss transition for multiple interests and no repeated uncovered fallback', async () => {
  vi.useFakeTimers()
  const { controller, connections, bootstrap } = setup()
  controller.register(request); controller.register({ topic: 'operations', tenantId: 'alpha' }); await controller.renew()
  const connection = connections[0]
  connection.heartbeat.mockResolvedValue(coverage(false))
  await vi.advanceTimersByTimeAsync(10000)
  expect(connection.reportLifecycle).toHaveBeenCalledExactlyOnceWith({ reconnects: 0, fallbacks: 1 })
  await vi.advanceTimersByTimeAsync(20000)
  expect(connection.reportLifecycle).toHaveBeenCalledTimes(1)
  expect(bootstrap).toHaveBeenCalledTimes(1)
})

it('subtracts only the immutable accepted submission while retaining transitions that arrived during the held report', async () => {
  vi.useFakeTimers()
  const held = deferred<boolean>()
  const { controller, connections } = setup(connection => connection.reportLifecycle.mockReturnValueOnce(held.promise))
  controller.register(request); await controller.renew()
  const connection = connections[0]
  await reconnect(connection)
  expect(Object.isFrozen(connection.reportLifecycle.mock.calls[0][0])).toBe(true)
  connection.heartbeat.mockResolvedValue(coverage(false))
  await vi.advanceTimersByTimeAsync(10000)
  expect(connection.reportLifecycle).toHaveBeenCalledTimes(1)
  held.resolve(true); await settle()
  await vi.advanceTimersByTimeAsync(10000)
  expect(connection.reportLifecycle).toHaveBeenLastCalledWith({ reconnects: 0, fallbacks: 1 })
})

it('fences held old reports across renewal without subtracting new counts or unlocking the newer singleflight', async () => {
  vi.useFakeTimers()
  const old = deferred<boolean>(); const current = deferred<boolean>()
  const { controller, connections } = setup()
  controller.register(request); await controller.renew()
  connections[0].reportLifecycle.mockReturnValueOnce(old.promise)
  await reconnect(connections[0])
  await controller.renew()
  connections[1].reportLifecycle.mockReturnValueOnce(current.promise)
  await vi.advanceTimersByTimeAsync(10000)
  expect(connections[1].reportLifecycle).toHaveBeenCalledExactlyOnceWith({ reconnects: 2, fallbacks: 2 })
  old.resolve(true); await settle()
  connections[1].heartbeat.mockResolvedValue(coverage(false))
  await vi.advanceTimersByTimeAsync(10000)
  expect(connections[1].reportLifecycle).toHaveBeenCalledTimes(1)
  current.resolve(true); await settle()
  await vi.advanceTimersByTimeAsync(10000)
  expect(connections[1].reportLifecycle).toHaveBeenLastCalledWith({ reconnects: 0, fallbacks: 1 })
  expect(controller.covered(request)).toBe(false)
  expect(controller.allowed('api', 'alpha')).toBe(true)
})

it('fences the old report epoch when automatic reconnect replaces the transport on the same adapter', async () => {
  vi.useFakeTimers()
  const held = deferred<boolean>()
  const { controller, connections } = setup(connection => connection.reportLifecycle.mockReturnValueOnce(held.promise))
  controller.register(request); await controller.renew()
  const connection = connections[0]
  await reconnect(connection); await reconnect(connection)
  held.resolve(true); await settle()
  await vi.advanceTimersByTimeAsync(10000)
  expect(connection.reportLifecycle).toHaveBeenLastCalledWith({ reconnects: 2, fallbacks: 2 })
})

it.each(['logout', 'revoked'] as const)('drops pending diagnostics immediately on %s and ignores the held completion', async (reason) => {
  vi.useFakeTimers()
  const held = deferred<boolean>()
  const { controller, connections, bootstrap } = setup(connection => connection.reportLifecycle.mockReturnValueOnce(held.promise))
  controller.register(request); await controller.renew()
  const connection = connections[0]
  await reconnect(connection)
  if (reason === 'logout') await controller.logout()
  else connection.sessionRevoked()
  held.resolve(false); await settle()
  connection.reconnected(); await controller.renew(); await vi.advanceTimersByTimeAsync(20000)
  expect(connection.reportLifecycle).toHaveBeenCalledTimes(1)
  expect(bootstrap).toHaveBeenCalledTimes(1)
  expect(controller.allowed('api', 'alpha')).toBe(false)
})

it('drops pending counts on dispose while counting a successful same-session resume after the first connection', async () => {
  vi.useFakeTimers()
  const held = deferred<boolean>()
  const { controller, connections } = setup(connection => connection.reportLifecycle.mockReturnValueOnce(held.promise))
  controller.register(request); await controller.renew()
  await reconnect(connections[0])
  controller.dispose(); await controller.renew()
  held.resolve(true); await settle()
  await vi.advanceTimersByTimeAsync(10000)
  expect(connections[1].reportLifecycle).toHaveBeenCalledExactlyOnceWith({ reconnects: 1, fallbacks: 0 })
})

it('never transfers held diagnostics across a QuerySession exit even for the same account and API', async () => {
  vi.useFakeTimers()
  const held = deferred<boolean>()
  const previous = setup(connection => connection.reportLifecycle.mockReturnValueOnce(held.promise), 'previous')
  previous.controller.register(request); await previous.controller.renew()
  await reconnect(previous.connections[0]); previous.deactivate()
  held.resolve(true); await settle()
  previous.connections[0].reconnected(); await previous.controller.renew()
  const next = setup(undefined, 'next'); next.controller.register(request); await next.controller.renew()
  await vi.advanceTimersByTimeAsync(20000)
  expect(previous.connections[0].reportLifecycle).toHaveBeenCalledTimes(1)
  expect(next.connections[0].reportLifecycle).not.toHaveBeenCalled()
  expect(next.controller.covered(request)).toBe(true)
})


it('retains an actual outage fallback across failed bootstrap and reports it after a successful replacement', async () => {
  vi.useFakeTimers()
  const { controller, connections, bootstrap } = setup()
  controller.register(request); await controller.renew()
  bootstrap.mockRejectedValueOnce(new ApiError('Unavailable', 503))
  connections[0].close()
  await vi.advanceTimersByTimeAsync(10000)
  expect(controller.status).toBe('offline')
  expect(connections).toHaveLength(1)
  expect(connections[0].reportLifecycle).not.toHaveBeenCalled()
  await vi.advanceTimersByTimeAsync(10000)
  expect(connections).toHaveLength(2)
  expect(connections[1].reportLifecycle).toHaveBeenCalledExactlyOnceWith({ reconnects: 1, fallbacks: 1 })
  expect(controller.covered(request)).toBe(true)
  expect(controller.allowed('api', 'alpha')).toBe(true)
})

it('keeps diagnostic attempts independent of expedited 250ms coverage heartbeats and adds no report timer', async () => {
  vi.useFakeTimers()
  const { controller, connections, session } = setup(connection => connection.reportLifecycle.mockResolvedValue(false))
  const context = { tenantId: 'alpha', deploymentId: 'deployment', revision: 7, generation: 2 }
  session.client.setQueryData(scopedQueryKey(session, ['api', 'alpha'], ['health', 'deployment', 7, 2]), 'current')
  controller.beginObservationRead('api', context)
  controller.register(request); await controller.renew()
  const connection = connections[0]
  await reconnect(connection)
  for (let version = 1; version <= 3; version++) {
    connection.heartbeat.mockResolvedValue(coverage(version === 2))
    connection.changed({ version: 1, ...request, revision: 7, generation: 2, observationVersion: version })
    await vi.advanceTimersByTimeAsync(250)
  }
  expect(connection.heartbeat).toHaveBeenCalledTimes(3)
  expect(connection.reportLifecycle).toHaveBeenCalledTimes(1)
  await vi.advanceTimersByTimeAsync(9250)
  expect(connection.heartbeat).toHaveBeenCalledTimes(3)
  expect(connection.reportLifecycle).toHaveBeenCalledTimes(1)
  await vi.advanceTimersByTimeAsync(750)
  expect(connection.heartbeat).toHaveBeenCalledTimes(4)
  expect(connection.reportLifecycle).toHaveBeenLastCalledWith({ reconnects: 1, fallbacks: 3 })
  session.client.clear()
})
