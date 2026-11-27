// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

export type RealtimeTopic =
  | 'source-builds'
  | 'operations'
  | 'web-services'
  | 'tenant-access'
  | 'tenant-members'
  | 'resource-groups'
  | 'workspace-inventory'
  | 'postgres'
  | 'redis'
  | 'compute-profiles'
  | 'storage-profiles'
  | 'deployment-targets'
  | 'compute-eligibility'
  | 'storage-eligibility'
  | 'deployment-target-eligibility'
  | 'secrets'
  | 'git'
  | 'search'
  | 'domains'
  | 'resource-connections'
  | 'runtime-observations'
  | 'storage-observations'
export interface RealtimeClientLifecycleReport {
  reconnects: number
  fallbacks: number
}
export const boundedLifecycleCount = (value: number) =>
  Number.isFinite(value) ? Math.min(100, Math.max(0, Math.floor(value))) : 0
export interface RealtimeSubscription {
  topic: RealtimeTopic
  tenantId?: string
  resourceId?: string
}
export interface RealtimeCoverage extends RealtimeSubscription {
  subscriptionId: string
  covered: boolean
}
export type RealtimeWireCoverage = Omit<RealtimeCoverage, 'tenantId' | 'resourceId'> & {
  tenantId?: string | null
  resourceId?: string | null
}
export const normalizeCoverage = (value: RealtimeWireCoverage): RealtimeCoverage => ({
  ...value,
  tenantId: value.tenantId ?? undefined,
  resourceId: value.resourceId ?? undefined,
})
export interface RealtimeChange {
  version: 1
  topic: RealtimeTopic
  tenantId?: string
  resourceId?: string
  resourceGroupId?: string
  revision?: number
  generation?: number
  observationVersion?: number
}
export type RealtimeWireChange = Omit<
  RealtimeChange,
  'tenantId' | 'resourceId' | 'resourceGroupId' | 'revision' | 'generation' | 'observationVersion'
> & {
  tenantId?: string | null
  resourceId?: string | null
  resourceGroupId?: string | null
  revision?: number | null
  generation?: number | null
  observationVersion?: number | null
}
export const normalizeChange = (value: RealtimeWireChange): RealtimeChange => ({
  ...value,
  tenantId: value.tenantId ?? undefined,
  resourceId: value.resourceId ?? undefined,
  resourceGroupId: value.resourceGroupId ?? undefined,
  revision: value.revision ?? undefined,
  generation: value.generation ?? undefined,
  observationVersion: value.observationVersion ?? undefined,
})
export type RealtimeStatus = 'offline' | 'connecting' | 'connected'
export const subscriptionKey = (value: RealtimeSubscription) =>
  JSON.stringify([value.topic, value.tenantId ?? null, value.resourceId ?? null])
export const acceptsChange = (subscription: RealtimeSubscription, event: RealtimeChange) =>
  event.version === 1 &&
  subscription.topic === event.topic &&
  subscription.tenantId === (event.tenantId ?? undefined) &&
  (subscription.topic === 'tenant-access'
    ? subscription.tenantId === undefined &&
      Boolean(subscription.resourceId) &&
      subscription.resourceId === event.resourceId
    : !subscription.resourceId || !event.resourceId || subscription.resourceId === event.resourceId)
