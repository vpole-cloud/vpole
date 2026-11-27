// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import type { RealtimeChange, RealtimeSubscription } from '../../realtime/types'

export function domainInterests(tenantId?: string): RealtimeSubscription[] {
  return [{ topic: 'domains', tenantId }]
}
export function matchesDomainQuery(key: readonly unknown[], event: RealtimeChange) {
  // Zone and binding IDs share the topic. Composite hostname ownership is deliberately
  // tenant-wide so deleting a binding also refreshes its old zone and service owner.
  return (
    event.topic === 'domains' &&
    ((key[0] === 'domains' && ['zones', 'zone', 'owners'].includes(String(key[1]))) ||
      (key[0] === 'web-services' && key[1] === 'hostnames'))
  )
}
