// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

// @vitest-environment jsdom
import { act, useLayoutEffect } from 'react'
import { createTestApp } from '../test/TestApp'
import { afterEach, expect, it, vi } from 'vitest'
import * as api from '../api'
import { useOverviewData } from '../modules/platform/useOverviewData'

vi.mock('../api', () => ({
  fetchResourceGroups: vi.fn(),
  fetchWebServices: vi.fn(),
  fetchProtectedGreeting: vi.fn(),
}))
const root = createTestApp().root
Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
afterEach(async () => {
  await act(async () => root.render(null))
  vi.resetAllMocks()
})
let state!: ReturnType<typeof useOverviewData>
function Probe({ tenantId, enabled = true }: { tenantId: string; enabled?: boolean }) {
  const value = useOverviewData({ apiBaseUrl: '/api', token: 'token', tenantId, enabled })
  useLayoutEffect(() => {
    state = value
  })
  return null
}

it('clears overview on tenant/logout and ignores an old successful response even when fetch ignores abort', async () => {
  let resolve!: (value: Awaited<ReturnType<typeof api.fetchWebServices>>) => void
  vi.mocked(api.fetchResourceGroups).mockResolvedValue({
    items: [],
    totalCount: 0,
    pageNumber: 1,
    pageSize: 10,
  })
  vi.mocked(api.fetchWebServices).mockImplementation((_base, _token, tenant) =>
    tenant === 'old'
      ? new Promise((complete) => {
          resolve = complete
        })
      : Promise.resolve({ items: [], totalCount: 0, pageNumber: 1, pageSize: 10 }),
  )
  await act(async () => root.render(<Probe tenantId="old" />))
  const signal = vi.mocked(api.fetchWebServices).mock.calls[0][3]!
  await act(async () => root.render(<Probe tenantId="new" />))
  expect(signal.aborted).toBe(true)
  await act(async () =>
    resolve({
      items: [{ id: 'foreign-canary' } as never],
      totalCount: 1,
      pageNumber: 1,
      pageSize: 10,
    }),
  )
  expect(state.data.webServices).toEqual([])
  await act(async () => root.render(<Probe tenantId="new" enabled={false} />))
  expect(state.data).toEqual({ resourceGroups: [], webServices: [] })
  expect(state.status).toBe('idle')
})

it('aborts identity check and excludes its old tenant message after the active scope changes', async () => {
  let resolve!: (value: string) => void
  vi.mocked(api.fetchResourceGroups).mockResolvedValue({
    items: [],
    totalCount: 0,
    pageNumber: 1,
    pageSize: 10,
  })
  vi.mocked(api.fetchWebServices).mockResolvedValue({
    items: [],
    totalCount: 0,
    pageNumber: 1,
    pageSize: 10,
  })
  vi.mocked(api.fetchProtectedGreeting).mockReturnValue(
    new Promise((complete) => {
      resolve = complete
    }),
  )
  await act(async () => root.render(<Probe tenantId="old" />))
  await act(async () => {
    void state.runIdentityCheck()
  })
  const signal = vi.mocked(api.fetchProtectedGreeting).mock.calls[0][2]!
  await act(async () => root.render(<Probe tenantId="new" />))
  expect(signal.aborted).toBe(true)
  await act(async () => resolve('old-identity-canary'))
  expect(state.identityMessage).toBe('')
  expect(state.identityStatus).toBe('idle')
})
