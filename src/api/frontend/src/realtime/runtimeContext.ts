import type { RealtimeChange } from './types'

export interface RuntimeContext { tenantId: string; deploymentId: string; revision: number; generation: number }
export interface RuntimeReadFence { epoch: number; floor: number }
export const runtimeContextKey = (context: RuntimeContext) => JSON.stringify([context.tenantId, context.deploymentId, context.revision, context.generation])
const positive = (value: unknown): value is number => typeof value === 'number' && Number.isSafeInteger(value) && value > 0
export function queryRuntimeContext(tenantId: unknown, key: readonly unknown[]): RuntimeContext | undefined {
  if (typeof tenantId !== 'string' || !tenantId || typeof key[1] !== 'string' || !key[1]) return
  const [revision, generation] = key[0] === 'health' ? [key[2], key[3]] : key[0] === 'runtime-discovery' ? [key[5], key[6]] : []
  return positive(revision) && positive(generation) ? { tenantId, deploymentId: key[1], revision, generation } : undefined
}
export function changeRuntimeContext(change: RealtimeChange): RuntimeContext | undefined {
  return change.topic === 'runtime-observations' && change.tenantId && change.resourceId && positive(change.revision) && positive(change.generation) &&
    typeof change.observationVersion === 'number' && Number.isSafeInteger(change.observationVersion) && change.observationVersion >= 0
    ? { tenantId: change.tenantId, deploymentId: change.resourceId, revision: change.revision, generation: change.generation } : undefined
}

export interface StorageContext { targetId: string; connectionVersion: number }
export type ObservationContext = RuntimeContext | StorageContext
export const isStorageContext = (context: ObservationContext): context is StorageContext => 'targetId' in context
export const observationContextKey = (context: ObservationContext) => isStorageContext(context)
  ? JSON.stringify(['storage', context.targetId, context.connectionVersion]) : runtimeContextKey(context)
export function queryObservationContext(tenantId: unknown, key: readonly unknown[]): ObservationContext | undefined {
  if (tenantId === undefined && key[0] === 'platform-storage' && key[1] === 'discovery' && typeof key[2] === 'string' && key[2] && positive(key[3]))
    return { targetId: key[2], connectionVersion: key[3] }
  return queryRuntimeContext(tenantId, key)
}
export function changeObservationContext(change: RealtimeChange): ObservationContext | undefined {
  if (change.topic === 'storage-observations' && change.tenantId === undefined && change.resourceId && positive(change.revision) &&
    typeof change.observationVersion === 'number' && Number.isSafeInteger(change.observationVersion) && change.observationVersion >= 0)
    return { targetId: change.resourceId, connectionVersion: change.revision }
  return changeRuntimeContext(change)
}
export const observationTopic = (topic: RealtimeChange['topic']) => topic === 'runtime-observations' || topic === 'storage-observations'
