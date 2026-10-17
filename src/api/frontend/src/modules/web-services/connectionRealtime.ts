import type { RealtimeChange, RealtimeSubscription } from '../../realtime/types'

export function connectionInterests(tenantId?: string): RealtimeSubscription[] {
  return [{ topic: 'resource-connections', tenantId }]
}
export function matchesConnectionQuery(key: readonly unknown[], event: RealtimeChange) {
  // Notices carry declaration IDs; source inventories and endpoint composites have
  // different ID positions. Re-read their public metadata across the affected tenant.
  return (
    event.topic === 'resource-connections' &&
    ((key[0] === 'connections' && key[1] === 'source') || key[0] === 'endpoints')
  )
}
