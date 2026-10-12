import type { RealtimeChange } from './types'
// Query parameters are provider owned; events invalidate metadata rather than inserting DTOs.
export function matchesPilotQuery(key: readonly unknown[], event: RealtimeChange) {
  const [namespace, kind, resourceId] = key
  if (event.topic === 'source-builds') {
    if (namespace === 'source-builds') return kind !== 'jobs' && kind !== 'job' || !event.resourceId || resourceId === event.resourceId
    return namespace === 'web-services' && ['detail', 'releases'].includes(String(kind)) && (!event.resourceId || resourceId === event.resourceId)
  }
  if (event.topic === 'operations') {
    if (namespace === 'operations') return (!key[1] || !event.resourceGroupId || key[1] === event.resourceGroupId) &&
      (!key[3] || !event.resourceId || key[3] === event.resourceId)
    return namespace === 'web-services' && ['detail', 'releases'].includes(String(kind)) && (!event.resourceId || resourceId === event.resourceId)
  }
  return namespace === 'web-services' && (kind === 'inventory' || ['detail', 'releases'].includes(String(kind)) && (!event.resourceId || resourceId === event.resourceId))
}
