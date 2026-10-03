// @vitest-environment jsdom
import { act, useLayoutEffect } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import * as api from '../api'
import { useResourceGroupsManager } from './useResourceGroupsManager'
import type { ResourceGroup } from '../types'

vi.mock('../api', () => ({
  fetchResourceGroups: vi.fn(), createResourceGroup: vi.fn(),
  updateResourceGroup: vi.fn(), deleteResourceGroup: vi.fn(),
}))
function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((complete) => { resolve = complete })
  return { promise, resolve }
}
const created: ResourceGroup = { id: 'new', name: 'mobile-new', description: '', tags: [], resourceCount: 0 }
const emptyPage = { items: [], totalCount: 0, pageNumber: 1, pageSize: 6 }
let root: Root
let state: ReturnType<typeof useResourceGroupsManager>
function Probe({ tenantId = 'tenant' }: { tenantId?: string }) {
  const value = useResourceGroupsManager({ apiBaseUrl: '/api', token: 'token', tenantId, enabled: true })
  useLayoutEffect(() => { state = value })
  return null
}
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  vi.resetAllMocks()
  root = createRoot(document.createElement('div'))
})
afterEach(async () => { await act(async () => { root.unmount() }) })
it('shows the created group and ignores an older list response arriving after creation', async () => {
  const oldList = deferred<Awaited<ReturnType<typeof api.fetchResourceGroups>>>()
  vi.mocked(api.fetchResourceGroups).mockReturnValue(oldList.promise)
  vi.mocked(api.createResourceGroup).mockResolvedValue(created)
  await act(async () => { root.render(<Probe />) })
  await act(async () => { await state.create(created) })
  expect(state.status).toBe('success')
  const existing = { ...created, id: 'existing', name: 'Existing' }
  await act(async () => { oldList.resolve({ ...emptyPage, items: [existing] }) })
  expect(state.items).toEqual([created, existing])
})
it('does not apply an old tenant list after a scope change', async () => {
  const oldList = deferred<Awaited<ReturnType<typeof api.fetchResourceGroups>>>()
  vi.mocked(api.fetchResourceGroups).mockReturnValueOnce(oldList.promise).mockResolvedValue(emptyPage)
  await act(async () => { root.render(<Probe tenantId="old" />) })
  await act(async () => { root.render(<Probe tenantId="new" />) })
  await act(async () => { oldList.resolve({ ...emptyPage, items: [created] }) })
  expect(state.items).toEqual([])
})
it('does not apply an old tenant mutation after a scope change', async () => {
  const mutation = deferred<ResourceGroup>()
  vi.mocked(api.fetchResourceGroups).mockResolvedValue(emptyPage)
  vi.mocked(api.createResourceGroup).mockReturnValue(mutation.promise)
  await act(async () => { root.render(<Probe tenantId="old" />) })
  let pending!: Promise<unknown>
  await act(async () => { pending = state.create(created) })
  await act(async () => { root.render(<Probe tenantId="new" />) })
  await act(async () => { mutation.resolve(created); await pending })
  expect(state.items).toEqual([])
  expect(state.submitStatus).toBe('idle')
})
it.each(['update', 'remove'] as const)('does not undo %s when a stale refresh finishes', async (operation) => {
  const stalePage = { ...emptyPage, items: [created] }
  vi.mocked(api.fetchResourceGroups).mockResolvedValue(stalePage)
  vi.mocked(api.updateResourceGroup).mockResolvedValue({ ...created, description: 'updated' })
  vi.mocked(api.deleteResourceGroup).mockResolvedValue(undefined)
  await act(async () => { root.render(<Probe />) })
  const oldList = deferred<Awaited<ReturnType<typeof api.fetchResourceGroups>>>()
  vi.mocked(api.fetchResourceGroups).mockReturnValueOnce(oldList.promise)
  let refresh!: Promise<void>
  await act(async () => { refresh = state.refresh() })
  await act(async () => {
    if (operation === 'update') await state.update(created.id, created)
    else await state.remove(created.id)
  })
  await act(async () => { oldList.resolve(stalePage); await refresh })
  expect(state.status).toBe('success')
  expect(state.items).toEqual(operation === 'update' ? [{ ...created, description: 'updated' }] : [])
})

it('loads every page for group choices and preserves them if a later refresh fails', async () => {
  const later = { ...created, id: 'later', name: 'Outside first page' }
  const firstPage = Array.from({ length: 200 }, (_, index) => ({ ...created, id: `group-${index}` }))
  vi.mocked(api.fetchResourceGroups).mockResolvedValueOnce({ ...emptyPage, pageSize: 200, totalCount: 201, items: firstPage })
    .mockResolvedValueOnce({ ...emptyPage, pageNumber: 2, pageSize: 200, totalCount: 201, items: [later] })
  await act(async () => root.render(<Probe />))
  expect(state.items).toEqual([...firstPage, later])
  expect(vi.mocked(api.fetchResourceGroups).mock.calls[1][4]).toEqual({ pageNumber: 2, pageSize: 200 })
  vi.mocked(api.fetchResourceGroups).mockRejectedValue(new Error('Refresh failed'))
  await act(async () => { await state.refresh() })
  expect(state.status).toBe('error'); expect(state.items).toEqual([...firstPage, later])
})
