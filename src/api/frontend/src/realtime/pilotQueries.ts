import type { RealtimeChange } from './types'
import { matchesSecretQuery } from '../modules/secrets/realtime'
import { matchesDomainQuery } from '../modules/domains/realtime'
import { matchesConnectionQuery } from '../modules/web-services/connectionRealtime'
import { matchesGitQuery } from '../modules/platform/gitRealtime'
import { matchesRuntimeQuery } from '../modules/operations/runtimeRealtime'
// Query parameters are provider owned; events invalidate metadata rather than inserting DTOs.
const sameResource = (id: unknown, event: RealtimeChange) => !event.resourceId || id === event.resourceId
const sameGroup = (id: unknown, groupId?: string) => !groupId || !id || id === groupId
const metadataProviders = new Set(['web-services', 'postgres', 'redis'])
function matchesOperations(key: readonly unknown[], event: RealtimeChange, resourceType?: string) {
  return sameGroup(key[1], event.resourceGroupId) && (!key[3] || sameResource(key[3], event)) && (!resourceType || !key[2] || key[2] === resourceType)
}
function matchesGroup(key: readonly unknown[], groupId?: string) {
  if (key[0] !== 'resource-groups') return false
  // Paginated group inventory and group-scoped registration inventory share a prefix.
  if (key[1] === 'choices' || key[1] === 'inventory' && typeof key[2] !== 'string') return true
  return ['detail', 'scopes', 'resources', 'inventory', 'complete-inventory'].includes(String(key[1])) && sameGroup(key[2], groupId)
}
export function matchesPilotQuery(key: readonly unknown[], event: RealtimeChange, meta?: Record<string, unknown>) {
  const [namespace, kind, resourceId] = key
  if (event.topic === 'runtime-observations') return matchesRuntimeQuery(key, event)
  if (event.topic === 'search') return namespace === 'search'
  if (event.topic === 'git') return matchesGitQuery(key, event)
  if (event.topic === 'domains') return matchesDomainQuery(key, event)
  if (event.topic === 'resource-connections') return matchesConnectionQuery(key, event)
  if (event.topic === 'compute-eligibility') return namespace === 'eligibility' && kind === 'compute-profiles'
  if (event.topic === 'secrets') return matchesSecretQuery(key, event)
  if (event.topic === 'deployment-target-eligibility') return namespace === 'eligibility' && kind === 'deployment-targets' || namespace === 'secrets' && kind === 'projection-targets'
  if (event.topic === 'storage-eligibility') return namespace === 'eligibility' && kind === 'storage-profiles'
  if (event.topic === 'storage-profiles' || event.topic === 'deployment-targets') return meta?.privileged === true && namespace === 'platform-storage' &&
    (kind === 'inventory' || event.topic === 'deployment-targets' && kind === 'discovery' && sameResource(resourceId, event))
  if (event.topic === 'resource-groups' || event.topic === 'workspace-inventory') {
    const groupId = event.resourceGroupId ?? (event.topic === 'resource-groups' ? event.resourceId : undefined)
    if (namespace === 'overview' || matchesGroup(key, groupId)) return true
    if (namespace === 'secrets' && kind === 'projection-targets') return sameGroup(meta?.resourceGroupId, groupId)
    if (namespace === 'eligibility') {
      // Target eligibility belongs to an isolation scope. Compute/storage profiles can be shared
      // by several groups on the same target; a single observer's group cannot narrow that cache.
      return kind !== 'deployment-targets' || sameGroup(meta?.resourceGroupId, groupId)
    }
    if (namespace === 'operations') return sameGroup(key[1], groupId) && (event.topic === 'resource-groups' || !key[3] || sameResource(key[3], event))
    return event.topic === 'workspace-inventory' && metadataProviders.has(String(namespace)) &&
      (kind === 'inventory' || kind === 'detail' && (event.resourceId ? resourceId === event.resourceId : sameGroup(meta?.resourceGroupId, groupId)))
  }
  if (event.topic === 'postgres' || event.topic === 'redis') {
    if (namespace === 'operations') return matchesOperations(key, event, event.topic === 'postgres' ? 'pole/postgresql' : 'pole/redis')
    return namespace === event.topic && (kind === 'inventory' || kind === 'detail' && sameResource(resourceId, event))
  }
  if (event.topic === 'source-builds') {
    if (namespace === 'source-builds') return kind === 'configurations' || ['jobs', 'job'].includes(String(kind)) && (!event.resourceId || resourceId === event.resourceId)
    return namespace === 'web-services' && ['detail', 'releases'].includes(String(kind)) && (!event.resourceId || resourceId === event.resourceId)
  }
  if (event.topic === 'operations') {
    if (namespace === 'operations') return matchesOperations(key, event)
    return metadataProviders.has(String(namespace)) && ['detail', ...(namespace === 'web-services' ? ['releases'] : [])].includes(String(kind)) && sameResource(resourceId, event)
  }
  if (event.topic === 'web-services' && namespace === 'operations') return matchesOperations(key, event, 'pole/web-service')
  return event.topic === 'web-services' && (namespace === 'overview' || namespace === 'web-services' && (kind === 'inventory' || ['detail', 'releases'].includes(String(kind)) && sameResource(resourceId, event)))
}
