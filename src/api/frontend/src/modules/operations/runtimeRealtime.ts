// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import type { RealtimeChange, RealtimeSubscription } from '../../realtime/types'
import { queryRuntimeContext } from '../../realtime/runtimeContext'
export function runtimeInterests(
  tenantId: string | undefined,
  resourceType: string | undefined,
  deploymentId: string,
): RealtimeSubscription[] {
  return tenantId &&
    ['pole/web-service', 'pole/redis', 'pole/postgresql'].includes(resourceType ?? '') &&
    deploymentId
    ? [{ topic: 'runtime-observations', tenantId, resourceId: deploymentId }]
    : []
}
export function matchesRuntimeQuery(key: readonly unknown[], change: RealtimeChange) {
  const context = queryRuntimeContext(change.tenantId, key)
  return (
    change.topic === 'runtime-observations' &&
    context !== undefined &&
    context.deploymentId === change.resourceId &&
    (change.revision === undefined || context.revision === change.revision) &&
    (change.generation === undefined || context.generation === change.generation)
  )
}
