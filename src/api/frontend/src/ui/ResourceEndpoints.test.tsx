// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

// @vitest-environment jsdom
import { act } from 'react'
import type { Root } from 'react-dom/client'
import { createTestApp } from '../test/TestApp'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { AppContext } from '../shell/AppContext'
import type { AppServices } from '../shell/contracts'
import type { ResourceEndpointDiscovery } from '../types'
import { RealtimeProvider } from '../realtime/RealtimeProvider'
import { createRealtimeConnection } from '../realtime/connection'
import { createRealtimeSession } from '../realtime/sessionApi'
import { FakeConnection } from '../test/fakeRealtimeConnection'
import { deferred } from '../test/http'
import { ResourceEndpoints } from './ResourceEndpoints'

vi.mock('../realtime/connection', () => ({ createRealtimeConnection: vi.fn() }))
vi.mock('../realtime/sessionApi', () => ({
  createRealtimeSession: vi.fn(),
  revokeRealtimeSession: vi.fn(async () => {}),
}))
let root: Root
let container: HTMLDivElement
const data: ResourceEndpointDiscovery = {
  resourceType: 'example/queue',
  resourceId: 'queue',
  displayName: 'Orders',
  availability: 'Supported',
  endpoints: [
    {
      id: 'consumer',
      displayName: 'Queue endpoint',
      protocol: 'amqp',
      transport: 'TCP',
      port: 5672,
      configuration: [{ key: 'virtualHost', displayName: 'Virtual host', value: '/app' }],
    },
  ],
  topology: { status: 'Eligible', reasonCode: null },
}
const fetchMock = vi.fn()
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  ;({ container, root } = createTestApp())
  fetchMock.mockReset()
  vi.stubGlobal('fetch', fetchMock)
})
afterEach(async () => {
  await act(async () => root.unmount())
  container.remove()
  vi.unstubAllGlobals()
  vi.useRealTimers()
})
async function render(tenantId = 'alpha') {
  await act(async () =>
    root.render(
      <AppContext.Provider
        value={{ api: { baseUrl: 'http://localhost', token: 'bearer', tenantId } } as AppServices}
      >
        <ResourceEndpoints
          resourceType="example/queue"
          resourceId="queue"
          source={{ resourceType: 'example/app', resourceId: 'source' }}
        />
      </AppContext.Provider>,
    ),
  )
}
const json = (value: unknown) => new Response(JSON.stringify(value))
it('renders custom provider metadata and sends both scoped resource identities', async () => {
  fetchMock.mockResolvedValue(json(data))
  await render()
  expect(container.textContent).toContain('Queue endpoint')
  expect(container.textContent).toContain('amqp · TCP')
  expect(container.textContent).toContain('Virtual host: /app')
  expect(container.textContent).toContain('Deployment targets match')
  const [url, init] = fetchMock.mock.calls[0]
  const query = new URL(url).searchParams
  expect(query.get('resourceType')).toBe('example/queue')
  expect(query.get('sourceResourceType')).toBe('example/app')
  expect(query.get('sourceResourceId')).toBe('source')
  expect(init.headers).toMatchObject({
    Authorization: 'Bearer bearer',
    'X-Pole-Tenant-ID': 'alpha',
  })
})
it.each(['empty', 'unsupported', 'unavailable', 'ineligible'])(
  'distinguishes %s discovery from other states',
  async (state) => {
    fetchMock.mockResolvedValue(
      state === 'unavailable'
        ? new Response('Unavailable', { status: 503 })
        : json({
            ...data,
            endpoints: state === 'ineligible' ? data.endpoints : [],
            availability: state === 'unsupported' ? 'Unsupported' : 'Supported',
            topology:
              state === 'ineligible'
                ? { status: 'Ineligible', reasonCode: 'DestinationTargetMissing' }
                : { status: 'NotEvaluated', reasonCode: null },
          }),
    )
    await render()
    expect(container.textContent).toContain(
      {
        empty: 'publishes no connection endpoints',
        unsupported: 'does not support connection endpoints',
        unavailable: 'Endpoint discovery is unavailable',
        ineligible: 'missing on at least one source deployment target',
      }[state]!,
    )
    if (state === 'unavailable')
      expect(container.textContent).not.toContain('publishes no connection endpoints')
  },
)
it('retains same-scope endpoints on refresh failure but discards them on tenant change', async () => {
  fetchMock.mockResolvedValueOnce(json(data))
  await render()
  fetchMock.mockResolvedValue(new Response('', { status: 503 }))
  await act(async () => container.querySelector('button')!.click())
  expect(container.textContent).toContain('Last fetched endpoints')
  expect(container.textContent).toContain('Queue endpoint')
  await render('beta')
  expect(container.textContent).not.toContain('Queue endpoint')
})

async function renderRealtime(type = 'pole/postgresql', active = true) {
  await act(async () =>
    root.render(
      <RealtimeProvider baseUrl="http://localhost" token="bearer" enabled userId="user">
        <AppContext.Provider
          value={
            {
              api: { baseUrl: 'http://localhost', token: 'bearer', tenantId: 'alpha' },
            } as AppServices
          }
        >
          <ResourceEndpoints
            resourceType={type}
            resourceId="database"
            source={{ resourceType: 'pole/web-service', resourceId: 'app' }}
            active={active}
          />
        </AppContext.Provider>
      </RealtimeProvider>,
    ),
  )
}
const tick = async (ms: number) => {
  await act(async () => vi.advanceTimersByTimeAsync(ms))
}
function realtimeFixture() {
  vi.useFakeTimers()
  Object.defineProperty(document, 'hidden', { configurable: true, value: false })
  const connection = new FakeConnection()
  vi.mocked(createRealtimeConnection).mockReturnValue(connection)
  vi.mocked(createRealtimeSession).mockResolvedValue({
    expiresAtUtc: new Date(Date.now() + 300000).toISOString(),
  })
  fetchMock.mockImplementation(async () => json(data))
  return connection
}

it('refetches declared metadata for exact source/destination and registry/placement hints with60second covered safety', async () => {
  const connection = realtimeFixture()
  await renderRealtime()
  await tick(1000)
  expect(connection.subscribe).toHaveBeenLastCalledWith([
    { topic: 'workspace-inventory', tenantId: 'alpha' },
    { topic: 'postgres', tenantId: 'alpha', resourceId: 'database' },
    { topic: 'web-services', tenantId: 'alpha', resourceId: 'app' },
  ])
  let count = fetchMock.mock.calls.length
  await tick(20000)
  expect(fetchMock).toHaveBeenCalledTimes(count)
  await act(async () =>
    connection.changed({ version: 1, topic: 'postgres', tenantId: 'alpha', resourceId: 'other' }),
  )
  await tick(500)
  expect(fetchMock).toHaveBeenCalledTimes(count)
  for (const event of [
    { topic: 'postgres' as const, resourceId: 'database' },
    { topic: 'web-services' as const, resourceId: 'app' },
    { topic: 'workspace-inventory' as const, resourceId: 'app' },
    { topic: 'workspace-inventory' as const },
  ]) {
    await act(async () => connection.changed({ version: 1, tenantId: 'alpha', ...event }))
    await tick(500)
    expect(fetchMock).toHaveBeenCalledTimes(++count)
  }
  await act(async () =>
    connection.changed({
      version: 1,
      topic: 'runtime-observations',
      tenantId: 'alpha',
      resourceId: 'database',
      revision: 1,
      generation: 1,
      observationVersion: 1,
    }),
  )
  await tick(500)
  expect(fetchMock).toHaveBeenCalledTimes(count)
  await renderRealtime('pole/postgresql', false)
  await tick(120000)
  expect(fetchMock).toHaveBeenCalledTimes(count)
  expect(connection.unsubscribe).toHaveBeenCalled()
})

it('coalesces endpoint bursts around a held read without cancellation and leaves unknown provider coverage on20second fallback', async () => {
  const connection = realtimeFixture()
  await renderRealtime()
  await tick(1000)
  const response = deferred<Response>()
  let held = true
  let signal: AbortSignal | undefined
  fetchMock.mockImplementation((_url, init) => {
    signal = init?.signal
    return held
      ? response.promise
      : Promise.resolve(json({ ...data, displayName: 'Committed metadata' }))
  })
  await act(async () =>
    connection.changed({
      version: 1,
      topic: 'postgres',
      tenantId: 'alpha',
      resourceId: 'database',
    }),
  )
  await tick(250)
  const count = fetchMock.mock.calls.length
  for (let index = 0; index < 20; index++)
    await act(async () =>
      connection.changed({
        version: 1,
        topic: 'workspace-inventory',
        tenantId: 'alpha',
        resourceId: 'database',
      }),
    )
  await tick(1000)
  expect(signal?.aborted).toBe(false)
  expect(fetchMock).toHaveBeenCalledTimes(count)
  held = false
  await act(async () => response.resolve(json(data)))
  await tick(500)
  expect(fetchMock).toHaveBeenCalledTimes(count + 1)
  await renderRealtime('custom/provider')
  await tick(500)
  expect(connection.unsubscribe).toHaveBeenCalled()
  const fallback = fetchMock.mock.calls.length
  await tick(20000)
  expect(fetchMock).toHaveBeenCalledTimes(fallback + 1)
})
