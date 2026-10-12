// @vitest-environment jsdom
import { act, useLayoutEffect } from 'react'
import type { Root } from 'react-dom/client'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import * as api from '../../api'
import { useWebServiceEligibility } from './useWebServiceEligibility'
import { createRealtimeConnection } from '../../realtime/connection'
import { RealtimeProvider } from '../../realtime/RealtimeProvider'
import { createRealtimeSession } from '../../realtime/sessionApi'
import { FakeConnection } from '../../test/fakeRealtimeConnection'
import { deferred } from '../../test/http'
import { createTestApp } from '../../test/TestApp'
import type { AvailableComputeProfile, AvailableDeploymentTarget, StorageProfile } from '../../types'
import { useResourceChoices } from '../../data/useResourceChoices'

vi.mock('../../api', () => ({ fetchAvailableComputeProfiles: vi.fn(), fetchAvailableDeploymentTargets: vi.fn(), fetchAvailableStorageProfiles: vi.fn(), fetchIsolationScopes: vi.fn(), fetchResourceGroups: vi.fn() }))
vi.mock('../../realtime/connection', () => ({ createRealtimeConnection: vi.fn() }))
vi.mock('../../realtime/sessionApi', () => ({ createRealtimeSession: vi.fn(), revokeRealtimeSession: vi.fn(async () => {}) }))
const target: AvailableDeploymentTarget = { id: 'target', displayName: 'Before', workloadConfigurationRevision: 1, hasPublicIngress: true }
const profile: AvailableComputeProfile = { id: 'small', displayName: 'Before profile', description: '', resources: { requests: { cpuMillis: 100, memoryMi: 128 }, limits: { cpuMillis: 200, memoryMi: 256 } }, architecture: 'Amd64', extendedResources: [], isDefault: true, revision: 1 }
const disk: StorageProfile = { id: 'disk', displayName: 'Disk', description: '', mediaType: 'SSD', performanceTier: 'Standard', isEnabled: true, isDefault: true,
  targetMappings: [{ deploymentTargetId: 'target', storageClassName: 'standard' }], revision: 1, allowedResourceTypes: ['pole/postgresql'], tenantAccess: { mode: 'AllTenants', tenantIds: [] } }
let root: Root
let connection: FakeConnection
let ws!: ReturnType<typeof useWebServiceEligibility>
let choices!: ReturnType<typeof useResourceChoices>
let hidden = false
beforeEach(() => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  vi.useFakeTimers(); vi.clearAllMocks(); hidden = false
  Object.defineProperty(document, 'hidden', { configurable: true, get: () => hidden })
  window.dispatchEvent(new Event('pageshow'))
  root = createTestApp().root; connection = new FakeConnection()
  vi.mocked(createRealtimeConnection).mockReturnValue(connection)
  vi.mocked(createRealtimeSession).mockResolvedValue({ expiresAtUtc: new Date(Date.now() + 300000).toISOString() })
  vi.mocked(api.fetchAvailableDeploymentTargets).mockResolvedValue([target])
  vi.mocked(api.fetchAvailableComputeProfiles).mockResolvedValue([profile])
  vi.mocked(api.fetchAvailableStorageProfiles).mockResolvedValue([disk])
  vi.mocked(api.fetchResourceGroups).mockResolvedValue({ items: [{ id: 'group', name: 'Group', description: '', tags: [], resourceCount: 0 }], pageNumber: 1, pageSize: 200, totalCount: 1 })
  vi.mocked(api.fetchIsolationScopes).mockResolvedValue([{ id: 'scope', resourceGroupId: 'group', name: 'Scope', lifecycle: 'active', isDefault: true, revision: 1, createdAtUtc: '', updatedAtUtc: '' }])
})
afterEach(async () => { await act(async () => root.unmount()); window.dispatchEvent(new Event('pageshow')); vi.useRealTimers() })
function WsProbe({ active = true }: { active?: boolean }) {
  const value = useWebServiceEligibility({ baseUrl: 'api', tenantId: 'tenant', token: 'current' }, active, 'group')
  useLayoutEffect(() => { ws = value }, [value])
  return null
}
function ChoicesProbe({ active = true }: { active?: boolean }) {
  const value = useResourceChoices({ baseUrl: 'api', tenantId: 'tenant', token: 'current' }, 'pole/postgresql', { groupId: 'group', isolationScopeId: 'scope', deploymentTargetId: 'target', computeProfileId: 'small', storageProfileId: 'disk' }, active)
  useLayoutEffect(() => { choices = value }, [value])
  return null
}
const renderWs = (active = true) => root.render(<RealtimeProvider baseUrl="api" token="current" enabled><WsProbe active={active} /></RealtimeProvider>)

it('updates persistent web-service dropdown queries on hints and releases them while hidden', async () => {
  await act(async () => renderWs())
  await act(async () => { await ws.loadAvailableTargets('scope', true); await ws.loadComputeProfiles('target'); await ws.loadComputeProfiles('second') })
  await act(async () => vi.advanceTimersByTimeAsync(500))
  expect(ws.availableTargets).toEqual([target]); expect(ws.computeProfilesStatusByTarget).toEqual({ target: 'success', second: 'success' })
  vi.mocked(api.fetchAvailableDeploymentTargets).mockResolvedValue([{ ...target, displayName: 'After' }])
  vi.mocked(api.fetchAvailableComputeProfiles).mockResolvedValue([{ ...profile, displayName: 'After profile', revision: 2 }])
  await act(async () => connection.changed({ version: 1, topic: 'workspace-inventory', tenantId: 'tenant', resourceId: 'database', resourceGroupId: 'group' }))
  await act(async () => vi.advanceTimersByTimeAsync(250))
  expect(ws.availableTargets[0].displayName).toBe('After')
  expect(ws.computeProfilesByTarget.target[0].revision).toBe(2); expect(ws.computeProfilesByTarget.second[0].revision).toBe(2)
  await act(async () => renderWs(false))
  vi.mocked(api.fetchAvailableDeploymentTargets).mockClear(); vi.mocked(api.fetchAvailableComputeProfiles).mockClear()
  await act(async () => connection.changed({ version: 1, topic: 'workspace-inventory', tenantId: 'tenant', resourceGroupId: 'group' }))
  await act(async () => vi.advanceTimersByTimeAsync(60000))
  expect(api.fetchAvailableDeploymentTargets).not.toHaveBeenCalled(); expect(api.fetchAvailableComputeProfiles).not.toHaveBeenCalled()
  await act(async () => renderWs())
  await act(async () => vi.advanceTimersByTimeAsync(500))
  expect(api.fetchAvailableDeploymentTargets).toHaveBeenCalled(); expect(api.fetchAvailableComputeProfiles).toHaveBeenCalled()
})

it('keeps a slow profile GET alive and applies one authoritative trailing profile read after a burst', async () => {
  await act(async () => renderWs())
  const old = deferred<AvailableComputeProfile[]>()
  let signal!: AbortSignal
  vi.mocked(api.fetchAvailableComputeProfiles).mockImplementationOnce((_base, _target, _token, _tenant, value) => { signal = value!; return old.promise })
  vi.mocked(api.fetchAvailableComputeProfiles).mockResolvedValue([{ ...profile, revision: 2 }])
  let reading!: Promise<AvailableComputeProfile[]>
  await act(async () => { reading = ws.loadComputeProfiles('target') })
  await act(async () => {
    for (let index = 0; index < 10; index++) connection.changed({ version: 1, topic: 'workspace-inventory', tenantId: 'tenant', resourceGroupId: 'group' })
    await vi.advanceTimersByTimeAsync(250)
  })
  expect(signal.aborted).toBe(false); expect(api.fetchAvailableComputeProfiles).toHaveBeenCalledTimes(1)
  await act(async () => { old.resolve([profile]); await reading })
  await act(async () => vi.advanceTimersByTimeAsync(1000))
  expect(api.fetchAvailableComputeProfiles).toHaveBeenCalledTimes(2)
  expect(ws.computeProfilesByTarget.target[0].revision).toBe(2)
})

it('refreshes canonical provisioning choices, preserves native resource filters and rejects a removed selected target without resetting selections', async () => {
  const render = (active = true) => root.render(<RealtimeProvider baseUrl="api" token="current" enabled><ChoicesProbe active={active} /></RealtimeProvider>)
  await act(async () => render())
  await act(async () => vi.advanceTimersByTimeAsync(500))
  expect(choices.allowed).toBe(true)
  expect(api.fetchAvailableDeploymentTargets).toHaveBeenLastCalledWith('api', 'scope', 'current', 'tenant', expect.any(AbortSignal), false, 'pole/postgresql')
  expect(api.fetchAvailableStorageProfiles).toHaveBeenLastCalledWith('api', 'pole/postgresql', 'target', 'current', 'tenant', expect.any(AbortSignal))
  vi.mocked(api.fetchAvailableDeploymentTargets).mockResolvedValue([])
  await act(async () => connection.changed({ version: 1, topic: 'resource-groups', tenantId: 'tenant', resourceId: 'group', resourceGroupId: 'group' }))
  await act(async () => vi.advanceTimersByTimeAsync(250))
  expect(choices.allowed).toBe(false); expect(choices.target).toBe('target')
  await act(async () => { hidden = true; document.dispatchEvent(new Event('visibilitychange')) })
  vi.mocked(api.fetchAvailableDeploymentTargets).mockClear()
  await act(async () => vi.advanceTimersByTimeAsync(120000))
  expect(api.fetchAvailableDeploymentTargets).not.toHaveBeenCalled()
})
