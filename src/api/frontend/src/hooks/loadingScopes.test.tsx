// @vitest-environment jsdom
import { act, useLayoutEffect } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '../api'
import { useTenants } from './useTenants'
import { useDomainZonesManager } from './useDomainZonesManager'
import { useStorageSettingsManager } from './useStorageSettingsManager'
import type { StorageProfileInput } from '../types'

vi.mock('../api', () => ({
  fetchTenants: vi.fn(),
  fetchDomainZones: vi.fn(),
  fetchAvailableDeploymentTargets: vi.fn(),
  createDomainZone: vi.fn(),
  deleteDomainZone: vi.fn(),
  createStorageProfile: vi.fn(),
  updateStorageProfile: vi.fn(),
  fetchStorageProfiles: vi.fn(),
  fetchPlatformDeploymentTargets: vi.fn(),
  fetchKubernetesStorageClasses: vi.fn(),
  fetchKubernetesPersistentVolumes: vi.fn(),
  fetchKubernetesPersistentVolumeClaims: vi.fn(),
}))

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((complete) => { resolve = complete })
  return { promise, resolve }
}

let root: Root
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  localStorage.clear()
  vi.resetAllMocks()
  root = createRoot(document.createElement('div'))
})
afterEach(async () => { await act(async () => { root.unmount() }) })

describe('loading scope changes', () => {
  it('aborts old target inventory and rejects a late response after selecting another target', async () => {
    const oldInventory = deferred<Awaited<ReturnType<typeof api.fetchKubernetesStorageClasses>>>()
    vi.mocked(api.fetchStorageProfiles).mockResolvedValue([])
    vi.mocked(api.fetchPlatformDeploymentTargets).mockResolvedValue([
      { id: 'a', displayName: 'East', enabled: true, acceptsWorkloads: true, isDefault: true },
      { id: 'b', displayName: 'West', enabled: true, acceptsWorkloads: true, isDefault: false },
    ])
    vi.mocked(api.fetchKubernetesStorageClasses).mockImplementation((_base, target) => target === 'a' ? oldInventory.promise : Promise.resolve([{ name: 'west', provisioner: 'test', isDefault: true, parameters: {}, metadata: {} }]))
    vi.mocked(api.fetchKubernetesPersistentVolumes).mockResolvedValue([])
    vi.mocked(api.fetchKubernetesPersistentVolumeClaims).mockResolvedValue([])
    let state!: ReturnType<typeof useStorageSettingsManager>
    function Probe() {
      const value = useStorageSettingsManager({ apiBaseUrl: '/api', token: 'token', enabled: true })
      useLayoutEffect(() => { state = value })
      return null
    }
    await act(async () => root.render(<Probe />))
    const signal = vi.mocked(api.fetchKubernetesStorageClasses).mock.calls[0][3]
    await act(async () => state.selectTarget('b'))
    expect(signal?.aborted).toBe(true)
    expect(state.storageClasses.map((item) => item.name)).toEqual(['west'])
    await act(async () => oldInventory.resolve([{ name: 'late-east', provisioner: 'test', isDefault: true, parameters: {}, metadata: {} }]))
    expect(state.deploymentTargetId).toBe('b')
    expect(state.storageClasses.map((item) => item.name)).toEqual(['west'])
    expect(state.status).toBe('success')
  })
  it('clears tenant discovery on logout and ignores an old authenticated response', async () => {
    const oldRequest = deferred<Awaited<ReturnType<typeof api.fetchTenants>>>()
    vi.mocked(api.fetchTenants).mockReturnValue(oldRequest.promise)
    let state!: ReturnType<typeof useTenants>
    function Probe({ enabled }: { enabled: boolean }) {
      const value = useTenants('/api', 'old-token', enabled)
      useLayoutEffect(() => { state = value })
      return null
    }
    await act(async () => { root.render(<Probe enabled />) })
    expect(state.status).toBe('loading')
    await act(async () => { root.render(<Probe enabled={false} />) })
    expect(state.status).toBe('idle')
    await act(async () => { oldRequest.resolve([{ id: 'old', name: 'Old', isDefault: true, role: 'Owner' }]) })
    expect(state.items).toEqual([])
    expect(state.status).toBe('idle')
    expect(localStorage.getItem('pole.activeTenantId')).toBeNull()
  })

  it('keeps the new token result when the previous discovery finishes later', async () => {
    const oldRequest = deferred<Awaited<ReturnType<typeof api.fetchTenants>>>()
    vi.mocked(api.fetchTenants).mockImplementation((_url, token) => token === 'old'
      ? oldRequest.promise
      : Promise.resolve([{ id: 'new', name: 'New', isDefault: true, role: 'Owner' }]))
    let state!: ReturnType<typeof useTenants>
    function Probe({ token }: { token: string }) {
      const value = useTenants('/api', token, true)
      useLayoutEffect(() => { state = value })
      return null
    }
    await act(async () => { root.render(<Probe token="old" />) })
    await act(async () => { root.render(<Probe token="new" />) })
    await act(async () => { oldRequest.resolve([{ id: 'old', name: 'Old', isDefault: true, role: 'Owner' }]) })
    expect(state.activeTenantId).toBe('new')
    expect(state.items.map((tenant) => tenant.id)).toEqual(['new'])
    expect(localStorage.getItem('pole.activeTenantId')).toBe('new')
  })

  it('aborts an old tenant domain-zone refresh when the tenant changes', async () => {
    const oldRequest = deferred<Awaited<ReturnType<typeof api.fetchDomainZones>>>()
    vi.mocked(api.fetchDomainZones).mockResolvedValue([])
    vi.mocked(api.fetchAvailableDeploymentTargets).mockResolvedValue([])
    let state!: ReturnType<typeof useDomainZonesManager>
    function Probe({ tenantId }: { tenantId: string }) {
      const value = useDomainZonesManager({ apiBaseUrl: '/api', token: 'token', tenantId, enabled: true })
      useLayoutEffect(() => { state = value })
      return null
    }
    await act(async () => { root.render(<Probe tenantId="old" />) })
    vi.mocked(api.fetchDomainZones).mockReturnValueOnce(oldRequest.promise)
    let refresh!: Promise<void>
    await act(async () => { refresh = state.refresh() })
    expect(state.status).toBe('loading')
    const oldSignal = vi.mocked(api.fetchDomainZones).mock.calls.at(-1)?.[3]
    await act(async () => { root.render(<Probe tenantId="new" />) })
    expect(oldSignal?.aborted).toBe(true)
    await act(async () => { oldRequest.resolve([]); await refresh })
    expect(state.status).toBe('success')
    expect(state.items).toEqual([])
    expect(api.fetchDomainZones).toHaveBeenLastCalledWith('/api', 'token', 'new', expect.any(AbortSignal))
  })
})


it('does not append a completed zone mutation to a different tenant', async () => {
  const mutation = deferred<Awaited<ReturnType<typeof api.createDomainZone>>>()
  vi.mocked(api.createDomainZone).mockReturnValue(mutation.promise)
  vi.mocked(api.fetchDomainZones).mockResolvedValue([])
  vi.mocked(api.fetchAvailableDeploymentTargets).mockResolvedValue([])
  let state!: ReturnType<typeof useDomainZonesManager>
  function Probe({ tenantId }: { tenantId: string }) {
    const value = useDomainZonesManager({ apiBaseUrl: '/api', token: 'token', tenantId, enabled: true })
    useLayoutEffect(() => { state = value })
    return null
  }
  await act(async () => { root.render(<Probe tenantId="old" />) })
  let create!: Promise<void>
  await act(async () => { create = state.create({ deploymentTargetId: 'target', label: 'old' }) })
  await act(async () => { root.render(<Probe tenantId="new" />) })
  await act(async () => {
    mutation.resolve({ id: 'zone', tenantId: 'old', deploymentTargetId: 'target', label: 'old', domain: 'old.test', kind: 'generated', revision: 1, createdAtUtc: '', updatedAtUtc: '' })
    await create
  })
  expect(state.items).toEqual([])
  expect(state.submitStatus).toBe('idle')
})

it.each(['create', 'update'] as const)('does not refresh an old authentication scope after storage %s completes', async (operation) => {
  const input: StorageProfileInput = {
    displayName: 'Old', description: '', mediaType: 'SSD', performanceTier: 'Standard',
    isEnabled: true, isDefault: false, targetMappings: [{ deploymentTargetId: 'target', storageClassName: 'standard' }], revision: 1,
    allowedResourceTypes: ['pole/redis'], tenantAccess: { mode: 'AllTenants', tenantIds: [] },
  }
  const mutation = deferred<Awaited<ReturnType<typeof api.createStorageProfile>>>()
  vi.mocked(api.createStorageProfile).mockReturnValue(mutation.promise)
  vi.mocked(api.updateStorageProfile).mockReturnValue(mutation.promise)
  vi.mocked(api.fetchStorageProfiles).mockResolvedValue([])
  vi.mocked(api.fetchPlatformDeploymentTargets).mockResolvedValue([])
  vi.mocked(api.fetchKubernetesStorageClasses).mockResolvedValue([])
  vi.mocked(api.fetchKubernetesPersistentVolumes).mockResolvedValue([])
  vi.mocked(api.fetchKubernetesPersistentVolumeClaims).mockResolvedValue([])
  let state!: ReturnType<typeof useStorageSettingsManager>
  function Probe({ token }: { token: string }) {
    const value = useStorageSettingsManager({ apiBaseUrl: '/api', token, enabled: true })
    useLayoutEffect(() => { state = value })
    return null
  }
  await act(async () => { root.render(<Probe token="old" />) })
  let submit!: Promise<void>
  await act(async () => { submit = operation === 'create' ? state.create(input) : state.update('profile', input) })
  await act(async () => { root.render(<Probe token="new" />) })
  await act(async () => { mutation.resolve({ ...input, id: 'profile', revision: 1 }); await submit })
  expect(api.fetchStorageProfiles).toHaveBeenCalledTimes(2)
  expect(api.fetchStorageProfiles).toHaveBeenLastCalledWith('/api', 'new', expect.any(AbortSignal))
  expect(state.profiles).toEqual([])
  expect(state.submitStatus).toBe('idle')
})
