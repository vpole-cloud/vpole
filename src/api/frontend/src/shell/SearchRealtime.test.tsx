// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

// @vitest-environment jsdom
import { act, useLayoutEffect } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { SessionQueries } from '../data/SessionQueries'
import { scopedQueryKey, useQuerySession, type QuerySession } from '../data/querySession'
import { RealtimeProvider } from '../realtime/RealtimeProvider'
import { createRealtimeConnection } from '../realtime/connection'
import { useRealtime } from '../realtime/context'
import type { RealtimeController } from '../realtime/controller'
import { createRealtimeSession } from '../realtime/sessionApi'
import { FakeConnection } from '../test/fakeRealtimeConnection'
import { deferred, jsonResponse, strictFetch } from '../test/http'
import type { PaginatedResponse, SearchHit } from '../types'
import { AppContext } from './AppContext'
import type { AppServices } from './contracts'
import { SearchPalette } from './SearchPalette'
import { createAppRegistry } from './moduleRegistry'
import { useResourceSearch } from '../modules/platform/useResourceSearch'

vi.mock('../realtime/connection', () => ({ createRealtimeConnection: vi.fn() }))
vi.mock('../realtime/sessionApi', () => ({
  createRealtimeSession: vi.fn(),
  revokeRealtimeSession: vi.fn(async () => {}),
}))
const http = strictFetch()
const registry = createAppRegistry([
  {
    id: 'test',
    views: [{ id: 'widgets', label: 'Widgets', component: () => null, access: 'tenant' }],
    resourceTypes: [{ id: 'test/widget', label: 'Widget', viewId: 'widgets' }],
  },
])
const hit = (id: string): SearchHit => ({
  resourceTypeId: 'test/widget',
  sourceType: 'Widget',
  sourceId: id,
  title: id,
  resourceGroupName: 'Production',
})
const page = (id: string, pageNumber: number): PaginatedResponse<SearchHit> => ({
  items: [hit(id)],
  totalCount: 2,
  pageNumber,
  pageSize: 1,
})
let root: Root
let container: HTMLDivElement
let connection: FakeConnection
let controller: RealtimeController
let session: QuerySession
let pages: PaginatedResponse<SearchHit>[]
let hidden: boolean
let heldSecond: Promise<Response> | undefined
let readSignal: AbortSignal | undefined
let probeRefresh: () => Promise<void>
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  vi.useFakeTimers()
  vi.clearAllMocks()
  http.reset()
  hidden = false
  Object.defineProperty(document, 'hidden', { configurable: true, get: () => hidden })
  window.dispatchEvent(new Event('pageshow'))
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
  container = document.createElement('div')
  document.body.append(container)
  root = createRoot(container)
  pages = [page('first', 1), page('second', 2)]
  heldSecond = undefined
  readSignal = undefined
  vi.mocked(createRealtimeConnection).mockImplementation(() => {
    connection = new FakeConnection()
    return connection
  })
  vi.mocked(createRealtimeSession).mockResolvedValue({
    expiresAtUtc: new Date(Date.now() + 300000).toISOString(),
  })
  http.fetch.mockImplementation((input, init) => {
    const url = new URL(String(input), 'http://localhost')
    if (url.pathname !== '/api/api/v1/search' || (init?.method && init.method !== 'GET'))
      return http.unhandled(input, init)
    const number = Number(url.searchParams.get('pageNumber'))
    if (!Number.isInteger(number) || number < 1 || number > pages.length)
      return http.unhandled(input, init)
    if (number === 2) {
      readSignal = init?.signal ?? undefined
      if (heldSecond) return heldSecond
    }
    return jsonResponse(pages[number - 1])
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
function QueryProbe({ text, pageNumber }: { text: string; pageNumber: number }) {
  const result = useResourceSearch(text, pageNumber)
  useLayoutEffect(() => {
    probeRefresh = result.refresh
  }, [result.refresh])
  return (
    <div>
      {result.hits.map((value) => (
        <span key={value.sourceId}>{value.title}</span>
      ))}
    </div>
  )
}
function Frame({ query }: { query?: { text: string; pageNumber: number } }) {
  const realtime = useRealtime()!
  const queries = useQuerySession()
  useLayoutEffect(() => {
    controller = realtime
    session = queries
  }, [realtime, queries])
  const services: AppServices = {
    auth: {
      initialized: true,
      authenticated: true,
      userId: 'user',
      isPlatformAdmin: false,
      login: () => {},
      logout: () => {},
    },
    api: { baseUrl: '/api', token: 'test-credential', tenantId: 'alpha' },
    tenant: {
      active: { id: 'alpha', name: 'Alpha', role: 'Member', isDefault: true },
      canManage: true,
    },
    navigation: {
      viewId: 'widgets',
      search: '',
      goTo: () => {},
      setSearch: () => {},
      openResource: () => {},
      resourceHref: () => '',
      registerUnsavedChanges: () => () => {},
    },
    theme: { value: 'light', set: () => {} },
    notifications: { publish: () => {} },
    resourceTypes: registry.resourceTypes,
  }
  return (
    <AppContext.Provider value={services}>
      {query ? <QueryProbe {...query} /> : <SearchPalette registry={registry} onClose={() => {}} />}
    </AppContext.Provider>
  )
}
const tick = async (ms: number) => {
  await act(async () => vi.advanceTimersByTimeAsync(ms))
}
const windowKey = () => scopedQueryKey(session, ['/api', 'alpha'], ['search', 'fi', 'window'])
const hits = () =>
  [...container.querySelectorAll('[role=option] strong')].map((item) => item.textContent)
async function open() {
  await act(async () =>
    root.render(
      <SessionQueries identity="/api:true:user">
        <RealtimeProvider baseUrl="/api" token="test-credential" enabled userId="user">
          <Frame />
        </RealtimeProvider>
      </SessionQueries>,
    ),
  )
  await act(async () => {
    const input = container.querySelector('input')!
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(input, 'fi')
    input.dispatchEvent(new Event('input', { bubbles: true }))
  })
  await tick(500)
  await tick(500)
  await act(async () =>
    [...container.querySelectorAll<HTMLButtonElement>('button')]
      .find((item) => item.textContent === 'More results')!
      .click(),
  )
  await tick(250)
  expect(hits()).toEqual(['first', 'second'])
}
const hint = async (tenantId = 'alpha') => {
  await act(async () => connection.changed({ version: 1, topic: 'search', tenantId }))
}
const renderProbe = async (text: string, pageNumber = 1) => {
  await act(async () =>
    root.render(
      <SessionQueries identity="/api:true:user">
        <RealtimeProvider baseUrl="/api" token="test-credential" enabled userId="user">
          <Frame query={{ text, pageNumber }} />
        </RealtimeProvider>
      </SessionQueries>,
    ),
  )
}

it('authoritatively replaces earlier and later search pages after a hint without invalidating other tenant or API windows', async () => {
  await open()
  const foreignKeys = [
    scopedQueryKey(session, ['/api', 'beta'], ['search', 'fi', 'window']),
    scopedQueryKey(session, ['/other', 'alpha'], ['search', 'fi', 'window']),
  ]
  for (const key of foreignKeys)
    session.client.setQueryData(key, { pages: [{ page: pages[0], epoch: 0 }], pageParams: [1] })
  http.fetch.mockClear()
  await hint('beta')
  await tick(250)
  expect(http.fetch).not.toHaveBeenCalled()
  pages = [page('renamed', 1), page('replacement', 2)]
  await hint()
  expect(hits()).toEqual(['first', 'second'])
  await tick(250)
  expect(hits()).toEqual(['renamed', 'replacement'])
  expect(
    http.fetch.mock.calls.map(([url]) =>
      new URL(String(url), 'http://localhost').searchParams.get('pageNumber'),
    ),
  ).toEqual(['1', '2'])
  for (const key of foreignKeys)
    expect(session.client.getQueryState(key)?.isInvalidated).toBe(false)
  expect(new Headers(http.fetch.mock.calls[0][1]?.headers).get('X-Pole-Tenant-ID')).toBe('alpha')
})

it('fences a held old page without aborting HTTP and resyncs the accumulated window once after a burst', async () => {
  await open()
  const held = deferred<Response>()
  heldSecond = held.promise
  let reading!: Promise<void>
  await act(async () => {
    reading = session.client.refetchQueries(
      { queryKey: windowKey(), exact: true },
      { cancelRefetch: false },
    )
  })
  const count = http.fetch.mock.calls.length
  const updatedAt = session.client.getQueryState(windowKey())?.dataUpdatedAt
  pages = [page('current-first', 1), page('current-second', 2)]
  await hint()
  await hint()
  await tick(250)
  await hint()
  await tick(250)
  expect(readSignal?.aborted).toBe(false)
  expect(http.fetch.mock.calls).toHaveLength(count)
  expect(hits()).toEqual(['first', 'second'])
  heldSecond = undefined
  await act(async () => {
    held.resolve(new Response(JSON.stringify(page('obsolete-second', 2))))
    await reading
  })
  await tick(250)
  expect(http.fetch.mock.calls).toHaveLength(count + 2)
  expect(hits()).toEqual(['current-first', 'current-second'])
  expect(session.client.getQueryState(windowKey())?.dataUpdatedAt).not.toBe(updatedAt)
  expect(JSON.stringify(session.client.getQueryData(windowKey()))).not.toContain('obsolete-second')
})

it('retains the last complete window when an authoritative resync fails and retries all pages', async () => {
  await open()
  const baseline = http.fetch.getMockImplementation()!
  http.fetch.mockImplementationOnce(() => jsonResponse({}, 503))
  pages = [page('fresh-first', 1), page('fresh-second', 2)]
  await hint()
  await tick(250)
  expect(hits()).toEqual(['first', 'second'])
  expect(container.textContent).toContain('Retry search')
  http.fetch.mockImplementation(baseline)
  await act(async () =>
    [...container.querySelectorAll<HTMLButtonElement>('button')]
      .find((item) => item.textContent === 'Retry search')!
      .click(),
  )
  expect(hits()).toEqual(['fresh-first', 'fresh-second'])
})

it('defers next-page additions until the held hint resync has replaced the entire head', async () => {
  pages = pages.map((value) => ({ ...value, totalCount: 3 }))
  await open()
  // The next page is now available, but the hint resync must replace the saved head first.
  pages = [
    { ...page('fresh-first', 1), totalCount: 3 },
    { ...page('fresh-second', 2), totalCount: 3 },
  ]
  const held = deferred<Response>()
  heldSecond = held.promise
  await hint()
  await tick(250)
  const count = http.fetch.mock.calls.length
  expect(hits()).toEqual(['first', 'second'])
  const more = [...container.querySelectorAll<HTMLButtonElement>('button')].find(
    (item) => item.textContent === 'More results',
  )!
  expect(more.disabled).toBe(true)
  await act(async () => more.click())
  await tick(250)
  expect(http.fetch.mock.calls).toHaveLength(count)
  expect(hits()).toEqual(['first', 'second'])
  heldSecond = undefined
  await act(async () => held.resolve(new Response(JSON.stringify(pages[1]))))
  expect(hits()).toEqual(['fresh-first', 'fresh-second'])
  expect(JSON.stringify(session.client.getQueryData(windowKey()))).not.toContain('"first"')
})

it('resyncs the complete window on reconnect and uses visible safety reads without heartbeat GET churn', async () => {
  await open()
  const epoch = controller.searchEpoch('/api', 'alpha')
  http.fetch.mockClear()
  await act(async () => connection.reconnecting())
  await act(async () => connection.reconnected())
  await tick(500)
  expect(controller.searchEpoch('/api', 'alpha')).toBeGreaterThan(epoch)
  expect(http.fetch).toHaveBeenCalledTimes(2)
  await tick(10000)
  expect(http.fetch).toHaveBeenCalledTimes(2)
  await act(async () => {
    hidden = true
    document.dispatchEvent(new Event('visibilitychange'))
  })
  http.fetch.mockClear()
  await tick(120000)
  expect(http.fetch).not.toHaveBeenCalled()
})

it('does not let a retained refresh callback read after hiding or changing text ownership', async () => {
  await renderProbe('fi')
  await tick(500)
  await tick(500)
  const retained = probeRefresh
  await act(async () => {
    hidden = true
    document.dispatchEvent(new Event('visibilitychange'))
  })
  http.fetch.mockClear()
  await act(async () => retained())
  expect(http.fetch).not.toHaveBeenCalled()
  await act(async () => {
    hidden = false
    document.dispatchEvent(new Event('visibilitychange'))
  })
  await renderProbe('fresh')
  http.fetch.mockClear()
  await act(async () => retained())
  expect(http.fetch).not.toHaveBeenCalled()
  await tick(500)
  await tick(500)
  expect(
    http.fetch.mock.calls.every(
      ([url]) => new URL(String(url), 'http://localhost').searchParams.get('query') === 'fresh',
    ),
  ).toBe(true)
})

it('loads the requested next page after a same-epoch held safety refetch finishes', async () => {
  pages = [...pages, page('third', 3)].map((value) => ({ ...value, totalCount: 3 }))
  await renderProbe('fi')
  await tick(500)
  await tick(500)
  await renderProbe('fi', 2)
  await tick(250)
  const held = deferred<Response>()
  heldSecond = held.promise
  let reading!: Promise<void>
  await act(async () => {
    reading = session.client.refetchQueries(
      { queryKey: windowKey(), exact: true },
      { cancelRefetch: false },
    )
  })
  const count = http.fetch.mock.calls.length
  await renderProbe('fi', 3)
  await tick(250)
  expect(http.fetch.mock.calls).toHaveLength(count)
  expect(readSignal?.aborted).toBe(false)
  heldSecond = undefined
  await act(async () => {
    held.resolve(new Response(JSON.stringify(pages[1])))
    await reading
  })
  await tick(250)
  expect(http.fetch.mock.calls).toHaveLength(count + 1)
  expect(container.textContent).toBe('firstsecondthird')
})

it('completes a requested window jump one page at a time without repeating a failed page', async () => {
  pages = [...pages, page('third', 3)].map((value) => ({ ...value, totalCount: 3 }))
  await renderProbe('fi')
  await tick(500)
  await tick(500)
  http.fetch.mockClear()
  await renderProbe('fi', 3)
  await tick(500)
  expect(container.textContent).toBe('firstsecondthird')
  expect(
    http.fetch.mock.calls.map(([url]) =>
      new URL(String(url), 'http://localhost').searchParams.get('pageNumber'),
    ),
  ).toEqual(['2', '3'])
})
