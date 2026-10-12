import type { RealtimeChange } from './types'
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
  if (event.topic === 'resource-groups' || event.topic === 'workspace-inventory') {
    const groupId = event.resourceGroupId ?? (event.topic === 'resource-groups' ? event.resourceId : undefined)
    if (namespace === 'overview' || matchesGroup(key, groupId)) return true
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
    if (namespace === 'source-builds') return kind !== 'jobs' && kind !== 'job' || !event.resourceId || resourceId === event.resourceId
    return namespace === 'web-services' && ['detail', 'releases'].includes(String(kind)) && (!event.resourceId || resourceId === event.resourceId)
  }
  if (event.topic === 'operations') {
    if (namespace === 'operations') return matchesOperations(key, event)
    return metadataProviders.has(String(namespace)) && ['detail', ...(namespace === 'web-services' ? ['releases'] : [])].includes(String(kind)) && sameResource(resourceId, event)
  }
  if (event.topic === 'web-services' && namespace === 'operations') return matchesOperations(key, event, 'pole/web-service')
  return event.topic === 'web-services' && (namespace === 'overview' || namespace === 'web-services' && (kind === 'inventory' || ['detail', 'releases'].includes(String(kind)) && sameResource(resourceId, event)))
}
