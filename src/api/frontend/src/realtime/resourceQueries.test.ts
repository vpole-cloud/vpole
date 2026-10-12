import { expect, it } from 'vitest'
import { matchesPilotQuery } from './pilotQueries'
import { groupInterests, operationInterests } from './resourceInterests'
import type { RealtimeChange } from './types'

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
