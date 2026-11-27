// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import type { RealtimeChange, RealtimeSubscription } from '../../realtime/types'
import { subscriptionKey } from '../../realtime/types'

interface EndpointIdentity {
  resourceType: string
  resourceId: string
}
const topics = {
  'pole/web-service': 'web-services',
  'pole/postgresql': 'postgres',
  'pole/redis': 'redis',
} as const
const providerTopic = (type: string) =>
  Object.hasOwn(topics, type) ? topics[type as keyof typeof topics] : undefined
export function endpointInterests(
  tenantId: string | undefined,
  destination: EndpointIdentity | undefined,
  source?: EndpointIdentity,
): RealtimeSubscription[] {
  return destination
    ? endpointCompositeInterests(tenantId, [destination, ...(source ? [source] : [])])
    : []
}
export function endpointCompositeInterests(
  tenantId: string | undefined,
  identities: readonly EndpointIdentity[],
  declarations = false,
): RealtimeSubscription[] {
  // Unknown provider metadata has no declared coverage contract. Keep the entire
  // composite on its existing fallback rather than acknowledging partial coverage.
  if (identities.some((identity) => !providerTopic(identity.resourceType))) return []
  const interests: RealtimeSubscription[] = [
    { topic: 'workspace-inventory', tenantId },
    ...(declarations ? [{ topic: 'resource-connections' as const, tenantId }] : []),
    ...identities.map((identity) => ({
      topic: providerTopic(identity.resourceType)!,
      tenantId,
      resourceId: identity.resourceId,
    })),
  ]
  return [...new Map(interests.map((interest) => [subscriptionKey(interest), interest])).values()]
}
export function matchesEndpointQuery(key: readonly unknown[], change: RealtimeChange) {
  if (key[0] !== 'endpoints') return false
  const metadata = change.topic === 'workspace-inventory'
  if (!metadata && !Object.values(topics).some((topic) => topic === change.topic)) return false
  const identities =
    key[1] === 'dependency-fields'
      ? Array.isArray(key[4])
        ? key[4]
        : []
      : [
          { resourceType: key[1], resourceId: key[2] },
          { resourceType: key[3], resourceId: key[4] },
        ]
  return identities.some(
    (identity) =>
      typeof identity?.resourceType === 'string' &&
      typeof identity?.resourceId === 'string' &&
      (metadata || providerTopic(identity.resourceType) === change.topic) &&
      (!change.resourceId || identity.resourceId === change.resourceId),
  )
}
