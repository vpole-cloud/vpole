import { expect, it } from 'vitest'
import { matchesPilotQuery } from './pilotQueries'
import { groupInterests, operationInterests } from './resourceInterests'
import type { RealtimeChange } from './types'
import { secretInterests } from '../modules/secrets/realtime'

it('targets Secret metadata by Secret ID, refreshes composite dependencies broadly and excludes sensitive reads', () => {
  const event: RealtimeChange = { version: 1, topic: 'secrets', tenantId: 'alpha', resourceId: 'secret' }
  for (const kind of ['detail', 'usage', 'projections', 'binding-placements', 'projection-targets']) {
    expect(matchesPilotQuery(['secrets', kind, 'secret'], event)).toBe(true)
    expect(matchesPilotQuery(['secrets', kind, 'other'], event)).toBe(false)
    expect(secretInterests('alpha', ['secrets', kind, 'secret'])).toEqual([{ topic: 'secrets', tenantId: 'alpha', resourceId: 'secret' }])
  }
  for (const kind of ['inventory', 'choices', 'consumer-bindings']) {
    expect(matchesPilotQuery(['secrets', kind, 'consumer'], event)).toBe(true)
    expect(secretInterests('alpha', ['secrets', kind, 'consumer'])).toEqual([{ topic: 'secrets', tenantId: 'alpha' }])
  }
  for (const key of [['secrets', 'values', 'secret'], ['secrets', 'reveal', 'secret'], ['postgres', 'credentials', 'database'], ['source-builds', 'logs', 'job']]) {
    expect(matchesPilotQuery(key, event)).toBe(false)
    expect(secretInterests('alpha', key)).toEqual([])
  }
  const targetEligibility: RealtimeChange = { version: 1, topic: 'deployment-target-eligibility', tenantId: 'alpha' }
  expect(matchesPilotQuery(['secrets', 'projection-targets', 'secret', 'scope', ['pole/redis']], targetEligibility)).toBe(true)
  expect(matchesPilotQuery(['eligibility', 'deployment-targets', 'pole/redis', 'scope', false], targetEligibility)).toBe(true)
  expect(matchesPilotQuery(['secrets', 'values', 'secret'], targetEligibility)).toBe(false)
})

it('maps logical registration IDs and group IDs to their own query dependencies', () => {
  const registration: RealtimeChange = { version: 1, topic: 'workspace-inventory', tenantId: 'tenant', resourceId: 'workload', resourceGroupId: 'group' }
  for (const key of [['resource-groups', 'inventory', 1, 8], ['resource-groups', 'choices'], ['resource-groups', 'resources', 'group', 2, 8],
    ['resource-groups', 'complete-inventory', 'group'], ['resource-groups', 'inventory', 'group', 'all'], ['resource-groups', 'detail', 'group'],
    ['postgres', 'detail', 'workload'], ['redis', 'inventory', 2, 8], ['overview']]) expect(matchesPilotQuery(key, registration)).toBe(true)
  for (const key of [['resource-groups', 'resources', 'other', 1, 8], ['resource-groups', 'detail', 'workload'], ['postgres', 'detail', 'other']])
    expect(matchesPilotQuery(key, registration)).toBe(false)
  const groupOnly: RealtimeChange = { ...registration, resourceId: undefined }
  expect(matchesPilotQuery(['redis', 'detail', 'one'], groupOnly, { resourceGroupId: 'group' })).toBe(true)
  expect(matchesPilotQuery(['redis', 'detail', 'two'], groupOnly, { resourceGroupId: 'other' })).toBe(false)
  expect(groupInterests('tenant', 'group')).toEqual([{ topic: 'resource-groups', tenantId: 'tenant', resourceId: 'group' }, { topic: 'workspace-inventory', tenantId: 'tenant' }])
})

it('refreshes matching group metadata and scope choices, retaining shared compute and storage dependencies', () => {
  const group: RealtimeChange = { version: 1, topic: 'resource-groups', tenantId: 'tenant', resourceId: 'group', resourceGroupId: 'group' }
  expect(matchesPilotQuery(['resource-groups', 'scopes', 'group'], group)).toBe(true)
  expect(matchesPilotQuery(['resource-groups', 'scopes', 'other'], group)).toBe(false)
  expect(matchesPilotQuery(['eligibility', 'deployment-targets', 'pole/postgresql', 'scope', false], group, { resourceGroupId: 'group' })).toBe(true)
  expect(matchesPilotQuery(['eligibility', 'deployment-targets', 'pole/postgresql', 'elsewhere', false], group, { resourceGroupId: 'other' })).toBe(false)
  expect(matchesPilotQuery(['eligibility', 'compute-profiles', 'pole/postgresql', 'shared-target'], group, { resourceGroupId: 'other' })).toBe(true)
  expect(matchesPilotQuery(['eligibility', 'storage-profiles', 'pole/redis', 'shared-target'], group, { resourceGroupId: 'other' })).toBe(true)
  expect(matchesPilotQuery(['postgres', 'detail', 'database'], group)).toBe(false)
})

it('maps provider and operation metadata without touching credentials, reveal values or runtime observations', () => {
  const event: RealtimeChange = { version: 1, topic: 'postgres', tenantId: 'tenant', resourceId: 'database', resourceGroupId: 'group' }
  expect(matchesPilotQuery(['postgres', 'inventory', 2, 8], event)).toBe(true)
  expect(matchesPilotQuery(['postgres', 'detail', 'database'], event)).toBe(true)
  expect(matchesPilotQuery(['postgres', 'detail', 'other'], event)).toBe(false)
  expect(matchesPilotQuery(['redis', 'detail', 'database'], event)).toBe(false)
  expect(matchesPilotQuery(['operations', 'group', 'pole/postgresql', 'database', 1], event)).toBe(true)
  expect(matchesPilotQuery(['operations', 'other', 'pole/postgresql', 'database', 1], event)).toBe(false)
  expect(matchesPilotQuery(['operations', null, 'pole/redis', null, 1], event)).toBe(false)
  expect(matchesPilotQuery(['postgres', 'detail', 'database'], { ...event, topic: 'operations' })).toBe(true)
  expect(matchesPilotQuery(['redis', 'detail', 'database'], { ...event, topic: 'operations' })).toBe(true)
  expect(matchesPilotQuery(['operations', null, 'pole/web-service', 'database', 1], { ...event, topic: 'web-services' })).toBe(true)
  expect(operationInterests('tenant', 'pole/postgresql', 'database')?.map(value => value.topic)).toEqual(['operations', 'workspace-inventory', 'postgres'])
  expect(operationInterests('tenant', 'pole/secret')).toBeUndefined()
  for (const topic of ['postgres', 'redis', 'workspace-inventory', 'resource-groups', 'operations'] as const)
    for (const key of [['postgres', 'credentials', 'database'], ['secrets', 'reveal', 'secret'], ['runtime-logs', 'database'], ['health', 'deployment'], ['endpoints', 'database']])
      expect(matchesPilotQuery(key, { ...event, topic })).toBe(false)
})

it('keeps private profile topics separate from public tenant storage eligibility', () => {
  const privateChange: RealtimeChange = { version: 1, topic: 'storage-profiles', resourceId: 'admin-profile' }
  expect(matchesPilotQuery(['platform-storage', 'inventory'], privateChange, { privileged: true })).toBe(true)
  expect(matchesPilotQuery(['platform-storage', 'inventory'], privateChange, { privileged: false })).toBe(false)
  expect(matchesPilotQuery(['eligibility', 'storage-profiles', 'pole/redis', 'target'], privateChange)).toBe(false)
  expect(matchesPilotQuery(['platform-storage', 'discovery', 'target'], privateChange, { privileged: true })).toBe(false)
  const publicChange: RealtimeChange = { version: 1, topic: 'storage-eligibility', tenantId: 'alpha' }
  expect(matchesPilotQuery(['eligibility', 'storage-profiles', 'pole/redis', 'target'], publicChange)).toBe(true)
  expect(matchesPilotQuery(['platform-storage', 'inventory'], publicChange, { privileged: true })).toBe(false)
  expect(matchesPilotQuery(['secrets', 'projections', 'secret'], publicChange)).toBe(false)
})

it('refreshes conservative hostname ownership and connection composites without exposing unrelated namespaces', () => {
  const domain: RealtimeChange = { version: 1, topic: 'domains', tenantId: 'alpha', resourceId: 'binding-or-zone' }
  for (const key of [['domains', 'zones'], ['domains', 'zone', 'old-zone'], ['domains', 'owners', 'old-zone', 2], ['web-services', 'hostnames', 'app', 'deployment']]) expect(matchesPilotQuery(key, domain)).toBe(true)
  for (const key of [['web-services', 'detail', 'app'], ['secrets', 'values', 'secret'], ['postgres', 'credentials', 'database']]) expect(matchesPilotQuery(key, domain)).toBe(false)
  const connection: RealtimeChange = { version: 1, topic: 'resource-connections', tenantId: 'alpha', resourceId: 'declaration' }
  for (const key of [['connections', 'source', 'app'], ['endpoints', 'dependency-fields', 'app', 'declaration:2'], ['endpoints', 'pole/postgresql', 'database', 'pole/web-service', 'app']]) expect(matchesPilotQuery(key, connection)).toBe(true)
  for (const key of [['postgres', 'credentials', 'database'], ['secrets', 'usage', 'secret'], ['runtime-logs', 'database']]) expect(matchesPilotQuery(key, connection)).toBe(false)
  const compute: RealtimeChange = { version: 1, topic: 'compute-eligibility', tenantId: 'alpha' }
  expect(matchesPilotQuery(['eligibility', 'compute-profiles', 'pole/web-service', 'target'], compute)).toBe(true)
  expect(matchesPilotQuery(['platform-storage', 'inventory'], compute, { privileged: true })).toBe(false)
  const recipe: RealtimeChange = { version: 1, topic: 'source-builds', tenantId: 'alpha', resourceId: 'app' }
  expect(matchesPilotQuery(['source-builds', 'configurations'], recipe)).toBe(true)
  expect(matchesPilotQuery(['source-builds', 'jobs', 'app'], recipe)).toBe(true)
  expect(matchesPilotQuery(['source-builds', 'jobs', 'other-app'], recipe)).toBe(false)
  expect(matchesPilotQuery(['source-builds', 'logs', 'app'], recipe)).toBe(false)
})
