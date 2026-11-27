// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import type { RealtimeChange, RealtimeSubscription } from '../../realtime/types'

// Values and reveal operations have no cache or realtime observer. Keep the metadata
// allowlist explicit so a future sensitive adapter cannot inherit an interest.
const details = new Set([
  'detail',
  'usage',
  'projections',
  'binding-placements',
  'projection-targets',
])
const composites = new Set(['inventory', 'choices', 'consumer-bindings'])
export function secretInterests(
  tenantId: string | undefined,
  key: readonly unknown[],
): RealtimeSubscription[] {
  if (key[0] !== 'secrets') return []
  if (composites.has(String(key[1]))) return [{ topic: 'secrets', tenantId }]
  return details.has(String(key[1])) && typeof key[2] === 'string'
    ? [{ topic: 'secrets', tenantId, resourceId: key[2] }]
    : []
}
export function matchesSecretQuery(key: readonly unknown[], event: RealtimeChange) {
  return (
    event.topic === 'secrets' &&
    key[0] === 'secrets' &&
    (composites.has(String(key[1])) ||
      (details.has(String(key[1])) && (!event.resourceId || key[2] === event.resourceId)))
  )
}
