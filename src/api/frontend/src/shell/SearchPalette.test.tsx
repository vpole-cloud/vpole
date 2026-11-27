// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

// @vitest-environment jsdom
import { act } from 'react'
import type { Root } from 'react-dom/client'
import { createTestApp } from '../test/TestApp'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { AppContext } from './AppContext'
import { SearchPalette } from './SearchPalette'
import { createAppRegistry } from './moduleRegistry'
import type { AppServices } from './contracts'
import { searchResources } from '../api'
import type { PaginatedResponse, SearchHit } from '../types'
vi.mock('../api', () => ({ searchResources: vi.fn() }))
const search = vi.mocked(searchResources)
const openResource = vi.fn()
const runCommand = vi.fn()
let root: Root
let container: HTMLDivElement
const close = vi.fn()
const registry = createAppRegistry([
  {
    id: 'test',
    views: [{ id: 'widgets', label: 'Widgets', component: () => null, access: 'tenant' }],
    resourceTypes: [{ id: 'test/widget', label: 'Widget', viewId: 'widgets' }],
    commands: [
      {
        id: 'widget.create',
        label: 'Create widget',
        viewId: 'widgets',
        available: ({ tenant }) => tenant.canManage,
        run: runCommand,
      },
    ],
  },
])
const hit = (id: string, type = 'test/widget'): SearchHit => ({
  resourceTypeId: type,
  sourceType: 'Widget',
  sourceId: id,
  title: id,
  resourceGroupName: 'Production',
})
const page = (
  items: SearchHit[],
  totalCount = items.length,
  pageNumber = 1,
): PaginatedResponse<SearchHit> => ({ items, totalCount, pageNumber, pageSize: 20 })
function services(id = 'alpha', canManage = true, token = 'token'): AppServices {
  return {
    auth: {
      initialized: true,
      authenticated: true,
      userId: 'user',
      isPlatformAdmin: false,
      login: vi.fn(),
      logout: vi.fn(),
    },
    api: { baseUrl: '/api', token, tenantId: id },
    tenant: {
      active: { id, name: id, role: canManage ? 'Owner' : 'Viewer', isDefault: true },
      canManage,
    },
    navigation: {
      viewId: 'widgets',
      search: '',
      goTo: vi.fn(),
      setSearch: vi.fn(),
      openResource,
      resourceHref: vi.fn(),
      registerUnsavedChanges: () => () => {},
    },
    theme: { value: 'light', set: vi.fn() },
    notifications: { publish: vi.fn() },
    resourceTypes: registry.resourceTypes,
  }
}
function render(id = 'alpha', canManage = true, token = 'token') {
  root.render(
    <AppContext.Provider value={services(id, canManage, token)}>
      <SearchPalette key={id} registry={registry} onClose={close} />
    </AppContext.Provider>,
  )
}
async function type(value: string) {
  await act(async () => {
    const input = container.querySelector('input')!
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(input, value)
    input.dispatchEvent(new Event('input', { bubbles: true }))
  })
}
async function searchNow() {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(201)
  })
}
function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((done) => {
    resolve = done
  })
  return { promise, resolve }
}
beforeEach(() => {
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
  vi.useFakeTimers()
  search.mockReset().mockResolvedValue(page([]))
  openResource.mockReset()
  runCommand.mockReset()
  close.mockReset()
  const app = createTestApp()
  container = app.container
  root = app.root
})
afterEach(async () => {
  await act(async () => root.unmount())
  container.remove()
  vi.useRealTimers()
  vi.restoreAllMocks()
})

it('opens a typed search result by keyboard and reports unavailable providers', async () => {
  search.mockResolvedValue(page([hit('resource'), hit('external', 'external/provider')]))
  await act(async () => render())
  await type('res')
  await searchNow()
  expect(search).toHaveBeenCalledWith('/api', 'res', 'token', 'alpha', expect.any(AbortSignal), 1)
  expect(container.textContent).toContain('Production')
  expect(container.textContent).toContain('UI unavailable')
  const input = container.querySelector('input')!
  await act(async () =>
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true })),
  )
  await act(async () =>
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })),
  )
  expect(openResource).toHaveBeenCalledWith({
    resourceTypeId: 'external/provider',
    resourceId: 'external',
  })
  expect(close).toHaveBeenCalledOnce()
})
it('cancels a previous query and ignores its late result', async () => {
  const old = deferred<PaginatedResponse<SearchHit>>()
  search.mockReturnValueOnce(old.promise).mockResolvedValue(page([hit('new-result')]))
  await act(async () => render())
  await type('old')
  await searchNow()
  const signal = search.mock.calls[0][4]!
  await type('new')
  expect(signal.aborted).toBe(true)
  await searchNow()
  await act(async () => old.resolve(page([hit('old-result')])))
  expect(container.textContent).toContain('new-result')
  expect(container.textContent).not.toContain('old-result')
})
it('remounts tenant search, aborts reads and clears previous results and query', async () => {
  const old = deferred<PaginatedResponse<SearchHit>>()
  search.mockReturnValueOnce(old.promise)
  await act(async () => render())
  await type('alpha-resource')
  await searchNow()
  const signal = search.mock.calls[0][4]!
  await act(async () => render('beta'))
  expect(signal.aborted).toBe(true)
  expect(container.querySelector('input')!.value).toBe('')
  await act(async () => old.resolve(page([hit('private-alpha')])))
  expect(container.textContent).not.toContain('private-alpha')
  expect(container.textContent).toContain('beta')
})
it('retains current results on pagination failure and retries the same page without duplicate identities', async () => {
  search
    .mockResolvedValueOnce(page([hit('first')], 2))
    .mockRejectedValueOnce(new Error('Search unavailable'))
    .mockResolvedValueOnce(page([hit('first'), hit('second')], 2, 2))
  await act(async () => render())
  await type('fi')
  await searchNow()
  const more = [...container.querySelectorAll('button')].find(
    (item) => item.textContent === 'More results',
  )!
  await act(async () => more.click())
  await searchNow()
  expect(container.textContent).toContain('Search unavailable')
  expect(container.textContent).toContain('first')
  await act(async () =>
    [...container.querySelectorAll('button')]
      .find((item) => item.textContent === 'Retry search')!
      .click(),
  )
  await searchNow()
  expect(
    [...container.querySelectorAll('[role="option"] strong')].map((item) => item.textContent),
  ).toEqual(['first', 'second'])
  expect(search.mock.calls.map((call) => call[5])).toEqual([1, 2, 2])
})
it('filters commands by role and restores focus on close', async () => {
  const opener = document.createElement('button')
  document.body.append(opener)
  opener.focus()
  await act(async () => render('alpha', false))
  expect(container.textContent).not.toContain('Create widget')
  expect(document.activeElement).toBe(container.querySelector('input'))
  await act(async () => render('alpha', true))
  expect(container.textContent).toContain('Create widget')
  await act(async () =>
    [...container.querySelectorAll<HTMLButtonElement>('[role="option"]')]
      .find((item) => item.textContent?.includes('Create widget'))!
      .click(),
  )
  expect(runCommand).toHaveBeenCalledOnce()
  await act(async () => root.render(null))
  expect(document.activeElement).toBe(opener)
  opener.remove()
})

it('retains accumulated results on token refresh and uses the refreshed token for the next page', async () => {
  search
    .mockResolvedValueOnce(page([hit('first')], 2))
    .mockResolvedValueOnce(page([hit('second')], 2, 2))
  await act(async () => render())
  await type('fi')
  await searchNow()
  await act(async () => render('alpha', true, 'fresh'))
  expect(container.querySelector('input')!.value).toBe('fi')
  expect(container.textContent).toContain('first')
  await act(async () =>
    [...container.querySelectorAll('button')]
      .find((item) => item.textContent === 'More results')!
      .click(),
  )
  await searchNow()
  expect(search).toHaveBeenLastCalledWith(
    '/api',
    'fi',
    'fresh',
    'alpha',
    expect.any(AbortSignal),
    2,
  )
  expect(
    [...container.querySelectorAll('[role="option"] strong')].map((item) => item.textContent),
  ).toEqual(['first', 'second'])
})
