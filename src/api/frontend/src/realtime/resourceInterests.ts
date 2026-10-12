import type { RealtimeSubscription } from './types'

export const groupInterests = (tenantId?: string, groupId?: string): RealtimeSubscription[] => [
  { topic: 'resource-groups', tenantId, resourceId: groupId },
  // Registry resourceId is a logical workload ID, never a group ID.
  { topic: 'workspace-inventory', tenantId },
]
export const resourceInterests = (topic: 'postgres' | 'redis', tenantId?: string, resourceId?: string): RealtimeSubscription[] => [
  { topic, tenantId, resourceId },
  { topic: 'workspace-inventory', tenantId },
  ...(resourceId ? [{ topic: 'operations' as const, tenantId, resourceId }] : []),
]
export function operationInterests(tenantId?: string, resourceType?: string, resourceId?: string): RealtimeSubscription[] | undefined {
  const topics = { 'pole/web-service': 'web-services', 'pole/postgresql': 'postgres', 'pole/redis': 'redis' } as const
  if (resourceType && !Object.hasOwn(topics, resourceType)) return undefined
  const providers = resourceType ? [topics[resourceType as keyof typeof topics]] : Object.values(topics)
  return [{ topic: 'operations', tenantId, resourceId }, { topic: 'workspace-inventory', tenantId, resourceId },
    ...providers.map(topic => ({ topic, tenantId, resourceId }))]
}
