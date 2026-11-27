// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

// @vitest-environment jsdom
import { act, useLayoutEffect } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { SessionQueries } from '../../data/SessionQueries'
import { useQuerySession, type QuerySession } from '../../data/querySession'
import { RealtimeProvider } from '../../realtime/RealtimeProvider'
import { createRealtimeConnection } from '../../realtime/connection'
import { useRealtime } from '../../realtime/context'
import type { RealtimeController } from '../../realtime/controller'
import { createRealtimeSession } from '../../realtime/sessionApi'
import type { RealtimeSubscription } from '../../realtime/types'
import { FakeConnection } from '../../test/fakeRealtimeConnection'
import { deferred, jsonResponse, strictFetch } from '../../test/http'
import type { StorageObservation } from './storageTypes'
import { useStorageSettingsManager } from './useStorageSettingsManager'

vi.mock('../../realtime/connection', () => ({ createRealtimeConnection: vi.fn() }))
vi.mock('../../realtime/sessionApi', () => ({
  createRealtimeSession: vi.fn(),
  revokeRealtimeSession: vi.fn(async () => {}),
}))
const http = strictFetch()
let root: Root
let container: HTMLDivElement
let connection: FakeConnection
let controller: RealtimeController
let session: QuerySession
let state: ReturnType<typeof useStorageSettingsManager>
let snapshot: StorageObservation
let connectionVersion: number
let held: Promise<Response> | undefined
let readSignal: AbortSignal | undefined
let covered: boolean
let hidden: boolean
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  vi.useFakeTimers()
  vi.clearAllMocks()
  http.reset()
  hidden = false
  covered = true
  held = undefined
  connectionVersion = 1
  Object.defineProperty(document, 'hidden', { configurable: true, get: () => hidden })
  window.dispatchEvent(new Event('pageshow'))
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
  snapshot = {
    deploymentTargetId: 'target',
    connectionVersion: 1,
    observationVersion: 1,
    checkedAtUtc: new Date().toISOString(),
    complete: true,
    storageClasses: [{ name: 'fast', provisioner: 'test', isDefault: true }],
    persistentVolumes: [],
    persistentVolumeClaims: [],
  }
  vi.mocked(createRealtimeConnection).mockImplementation(() => {
    connection = new FakeConnection()
    let requests: RealtimeSubscription[] = []
    const acknowledgements = () =>
      requests.map((request, index) => ({
        ...request,
        subscriptionId: String(index),
        covered: request.topic !== 'storage-observations' || covered,
      }))
    connection.subscribe.mockImplementation(async (values) => {
      requests = values
      return acknowledgements()
    })
    connection.heartbeat.mockImplementation(async () => acknowledgements())
    connection.unsubscribe.mockImplementation(async () => {
      requests = []
    })
    return connection
  })
  vi.mocked(createRealtimeSession).mockResolvedValue({
    expiresAtUtc: new Date(Date.now() + 300000).toISOString(),
  })
  http.fetch.mockImplementation((input, init) => {
    const url = new URL(String(input), 'http://localhost')
    if (init?.method && init.method !== 'GET') return http.unhandled(input, init)
    if (url.pathname.endsWith('/storage/profiles')) return jsonResponse([])
    if (url.pathname.endsWith('/deployment-targets'))
      return jsonResponse([
        {
          id: 'target',
          displayName: 'Target',
          enabled: true,
          acceptsWorkloads: true,
          isDefault: true,
          connection: { kind: 'Kubeconfig', version: connectionVersion },
        },
      ])
    if (url.pathname.endsWith('/storage/observation')) {
      readSignal = init?.signal ?? undefined
      return held ?? jsonResponse({ ...snapshot, checkedAtUtc: new Date().toISOString() })
    }
    return http.unhandled(input, init)
  })
  vi.stubGlobal('fetch', http.fetch)
})
afterEach(async () => {
  await act(async () => root.unmount())
  container.remove()
  window.dispatchEvent(new Event('pageshow'))
  http.verify()
  vi.unstubAllGlobals()
  vi.useRealTimers()
})
function Probe({
  token,
  enabled,
  publish = true,
}: {
  token: string
  enabled: boolean
  publish?: boolean
}) {
  const value = useStorageSettingsManager({ apiBaseUrl: '/api', token, enabled })
  const realtime = useRealtime()!
  const queries = useQuerySession()
  useLayoutEffect(() => {
    if (publish) {
      state = value
      controller = realtime
      session = queries
    }
  }, [value, realtime, queries, publish])
  return null
}
async function render(token = 'first', admin = true, first = false) {
  await act(async () =>
    root.render(
      <SessionQueries identity="/api:true:user">
        <RealtimeProvider
          baseUrl="/api"
          token={token}
          platformAdminClaim={admin}
          enabled
          userId="user"
        >
          {first && <Probe key="initiator" token={token} enabled={admin} publish={false} />}
          <Probe key="survivor" token={token} enabled={admin} />
        </RealtimeProvider>
      </SessionQueries>,
    ),
  )
}
const tick = async (ms: number) => {
  await act(async () => vi.advanceTimersByTimeAsync(ms))
}
const reads = () =>
  http.fetch.mock.calls.filter(([url]) => String(url).includes('/storage/observation')).length
const hint = async (version: number, revision = 1, id = 'target') => {
  await act(async () =>
    connection.changed({
      version: 1,
      topic: 'storage-observations',
      resourceId: id,
      revision,
      observationVersion: version,
    }),
  )
}
async function open() {
  await render()
  await tick(1000)
  expect(state.discoveryCurrent).toBe(true)
}

it('fences a held storage read on bursts without cancellation and accepts only the authoritative retained version', async () => {
  await open()
  const old = structuredClone(snapshot)
  const response = deferred<Response>()
  held = response.promise
  let reading!: Promise<unknown>
  await act(async () => {
    reading = state.refresh()
  })
  for (let version = 2; version <= 20; version++) await hint(version)
  expect(readSignal?.aborted).toBe(false)
  expect(state.discoveryCurrent).toBe(false)
  snapshot = { ...snapshot, observationVersion: 20, storageClasses: [] }
  held = undefined
  await act(async () => {
    response.resolve(new Response(JSON.stringify(old)))
    await reading
  })
  await tick(500)
  expect(state.discoveryCurrent).toBe(true)
  expect(state.storageClasses).toEqual([])
  expect(connection.heartbeat).toHaveBeenCalledTimes(1)
  expect(controller.covered({ topic: 'storage-observations', resourceId: 'target' })).toBe(true)
})

it('fences target catalog changes before inventory refresh and preserves prior display through credential rotation', async () => {
  await open()
  const response = deferred<Response>()
  held = response.promise
  const old = structuredClone(snapshot)
  let reading!: Promise<unknown>
  await act(async () => {
    reading = state.refresh()
  })
  await act(async () =>
    connection.changed({
      version: 1,
      topic: 'deployment-targets',
      resourceId: 'target',
      revision: 99,
    }),
  )
  expect(state.discoveryCurrent).toBe(false)
  expect(state.hasDiscovery).toBe(true)
  expect(state.storageClasses[0].name).toBe('fast')
  connectionVersion = 2
  snapshot = {
    ...snapshot,
    connectionVersion: 2,
    observationVersion: 21,
    storageClasses: [{ name: 'rotated', provisioner: 'test', isDefault: true }],
  }
  held = undefined
  await act(async () => {
    response.resolve(new Response(JSON.stringify(old)))
    await reading
  })
  await tick(1000)
  expect(state.discoveryCurrent).toBe(true)
  expect(state.storageClasses[0].name).toBe('rotated')
  await hint(100, 1)
  await tick(500)
  expect(state.discoveryCurrent).toBe(true)
  expect(state.storageClasses[0].name).toBe('rotated')
})

it('rejects mismatched identities and incomplete evidence without fabricating an empty successful inventory', async () => {
  await open()
  snapshot = { ...snapshot, deploymentTargetId: 'other' }
  await act(async () => state.refresh())
  expect(state.discoveryCurrent).toBe(false)
  expect(state.storageClasses[0].name).toBe('fast')
  snapshot = { ...snapshot, deploymentTargetId: 'target', complete: false, storageClasses: [] }
  await act(async () => state.refresh())
  expect(state.discoveryCurrent).toBe(false)
  expect(state.hasDiscovery).toBe(true)
  expect(state.storageClasses[0].name).toBe('fast')
  snapshot = {
    ...snapshot,
    storageClasses: [{ name: 'partial-foreign', provisioner: 'test', isDefault: true }],
  }
  await act(async () => state.refresh())
  expect(state.discoveryCurrent).toBe(false)
  expect(state.storageClasses[0].name).toBe('fast')
  snapshot = { ...snapshot, complete: true, observationVersion: null, storageClasses: [] }
  await act(async () => state.refresh())
  expect(state.discoveryCurrent).toBe(true)
  expect(state.storageClasses).toEqual([])
})

it('retains30second safety reads without heartbeat GET churn, uses manual uncovered fallback and releases hidden interests', async () => {
  await open()
  const count = reads()
  await tick(27000)
  expect(reads()).toBe(count)
  await tick(3000)
  expect(reads()).toBe(count + 1)
  covered = false
  await act(async () => {
    connection.reconnecting()
    connection.reconnected()
  })
  await tick(500)
  const fallback = reads()
  await tick(60000)
  expect(reads()).toBe(fallback)
  await act(async () => state.refresh())
  expect(reads()).toBe(fallback + 1)
  await act(async () => {
    hidden = true
    document.dispatchEvent(new Event('visibilitychange'))
  })
  http.fetch.mockClear()
  await tick(120000)
  expect(http.fetch).not.toHaveBeenCalled()
  expect(connection.stop).toHaveBeenCalled()
})

it('clears privileged evidence and held callbacks on admin loss and restores only fresh authority', async () => {
  await open()
  const response = deferred<Response>()
  held = response.promise
  let reading!: Promise<unknown>
  await act(async () => {
    reading = state.refresh()
  })
  const old = structuredClone(snapshot)
  await render('readonly', false)
  expect(readSignal?.aborted).toBe(true)
  expect(state.hasDiscovery).toBe(false)
  expect(
    session.client
      .getQueryCache()
      .getAll()
      .filter((query) => query.meta?.privileged)
      .every((query) => query.state.data === undefined),
  ).toBe(true)
  held = undefined
  snapshot = {
    ...snapshot,
    storageClasses: [{ name: 'restored', provisioner: 'test', isDefault: true }],
  }
  const restored = deferred<Response>()
  held = restored.promise
  await render('fresh-admin', true)
  await tick(1000)
  expect(state.hasDiscovery).toBe(false)
  held = undefined
  await act(async () => restored.resolve(new Response(JSON.stringify(snapshot))))
  await tick(1000)
  await act(async () => {
    response.resolve(new Response(JSON.stringify(old)))
    await reading
  })
  expect(state.discoveryCurrent).toBe(true)
  expect(state.storageClasses[0].name).toBe('restored')
})

it('accepts a shared storage response when its initiating observer leaves', async () => {
  await render('first', true, true)
  await tick(1000)
  const response = deferred<Response>()
  held = response.promise
  let reading!: Promise<unknown>
  await act(async () => {
    reading = state.refresh()
  })
  const signal = readSignal
  await render('first', true, false)
  expect(signal?.aborted).toBe(false)
  await act(async () => {
    response.resolve(new Response(JSON.stringify(snapshot)))
    await reading
  })
  await tick(1000)
  expect(state.discoveryCurrent).toBe(true)
})

it('expires uncovered storage evidence after45seconds and restores freshness only through authoritative manual/visibility reads', async () => {
  covered = false
  snapshot = { ...snapshot, observationVersion: null }
  await open()
  const count = reads()
  await tick(44000)
  expect(state.discoveryCurrent).toBe(true)
  await tick(2000)
  expect(state.discoveryCurrent).toBe(false)
  expect(state.hasDiscovery).toBe(true)
  expect(reads()).toBe(count)
  await act(async () => state.refresh())
  expect(state.discoveryCurrent).toBe(true)
  expect(reads()).toBe(count + 1)
  await act(async () => {
    hidden = true
    document.dispatchEvent(new Event('visibilitychange'))
  })
  await tick(60000)
  expect(reads()).toBe(count + 1)
  const resumed = deferred<Response>()
  held = resumed.promise
  await act(async () => {
    hidden = false
    document.dispatchEvent(new Event('visibilitychange'))
  })
  await tick(1000)
  expect(state.discoveryCurrent).toBe(false)
  expect(state.storageClasses[0].name).toBe('fast')
  held = undefined
  await act(async () =>
    resumed.resolve(
      new Response(JSON.stringify({ ...snapshot, checkedAtUtc: new Date().toISOString() })),
    ),
  )
  await tick(1000)
  expect(state.discoveryCurrent).toBe(true)
  expect(reads()).toBeGreaterThan(count + 1)
})

it('does not revive freshness when an already expired snapshot arrives after a slow uncovered read', async () => {
  covered = false
  snapshot = { ...snapshot, observationVersion: null }
  await open()
  await tick(35000)
  const response = deferred<Response>()
  held = response.promise
  const captured = { ...snapshot, checkedAtUtc: new Date().toISOString() }
  let reading!: Promise<unknown>
  await act(async () => {
    reading = state.refresh()
  })
  await tick(90000)
  expect(state.discoveryCurrent).toBe(false)
  expect(readSignal?.aborted).toBe(false)
  held = undefined
  await act(async () => {
    response.resolve(new Response(JSON.stringify(captured)))
    await reading
  })
  expect(state.hasDiscovery).toBe(true)
  expect(state.discoveryCurrent).toBe(false)
  await act(async () => state.refresh())
  expect(state.discoveryCurrent).toBe(true)
})
