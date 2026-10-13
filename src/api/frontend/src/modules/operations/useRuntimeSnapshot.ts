import { useCallback } from 'react'
import { ApiError } from '../../api/transport'
import { useApiScope } from '../../data/useApiScope'
import { useScopedQuery } from '../../data/useScopedQuery'
import { useRealtime } from '../../realtime/context'
import { useAppServices } from '../../shell/AppContext'
import type { ResourceDeploymentDiagnostics, ResourceDeploymentObservation, ResourcePlacement } from './types'
import { fetchResourceDiagnostics, fetchResourceObservation } from './api'
import { runtimeInterests } from './runtimeRealtime'
import type { RuntimeContext } from '../../realtime/runtimeContext'

interface Identity { placement: ResourcePlacement; resourceType?: string; resourceId?: string; active: boolean }
interface Read<T> { snapshot: T; epoch?: number }
type Snapshot = ResourceDeploymentObservation | ResourceDeploymentDiagnostics

function useSnapshot<T extends Snapshot>(key: readonly unknown[], { placement, resourceType, resourceId, active }: Identity, load: (base: string, id: string, token: string | undefined, tenantId: string | undefined, signal: AbortSignal) => Promise<T>, diagnostics: boolean) {
  const { api } = useAppServices()
  const origin = useApiScope(api)
  const controller = useRealtime()
  const realtime = runtimeInterests(api.tenantId, resourceType, placement.deploymentId)
  const context: RuntimeContext | undefined = realtime.length && controller?.ownsApi(api.baseUrl) ? {
    tenantId: api.tenantId!, deploymentId: placement.deploymentId, revision: placement.deploymentRevision ?? 1, generation: placement.desiredGeneration,
  } : undefined
  const query = useScopedQuery<Read<T>>({ scope: [api.baseUrl, api.tenantId], key, active, realtime, interval: 20000, coveredInterval: 30000,
    load: async signal => {
      const started = context ? controller!.beginRuntimeRead(api.baseUrl, context) : undefined
      const snapshot = await load(api.baseUrl, placement.deploymentId, origin.credential(), api.tenantId, signal)
      // This response belongs to the shared query, which can outlive its initiating
      // observer. Session, authorization and context fences still protect the cache.
      if (signal.aborted || !origin.session.isActive() || controller && (!controller.allowed(api.baseUrl, api.tenantId) ||
        origin.privileged && !controller.platformAllowed(api.baseUrl))) throw new DOMException('Aborted', 'AbortError')
      const same = snapshot.deploymentId === placement.deploymentId && snapshot.deploymentRevision === (placement.deploymentRevision ?? 1) && snapshot.desiredGeneration === placement.desiredGeneration
      const detail = snapshot as ResourceDeploymentDiagnostics
      if (!same || diagnostics && (detail.resourceType !== resourceType || detail.resourceId !== resourceId || detail.targetId !== placement.deploymentTargetId))
        throw new ApiError('This deployment changed. Refresh operations before opening runtime diagnostics.', 409)
      if (!Number.isFinite(Date.parse(snapshot.checkedAtUtc))) throw new ApiError('Runtime state response is unavailable. Refresh to reconnect.', 502)
      if (context && !controller!.acceptRuntimeRead(api.baseUrl, context, started!, snapshot.observationVersion, signal)) throw new DOMException('Aborted', 'AbortError')
      return { snapshot, epoch: started?.epoch }
    } })
  const resource = query.resource?.snapshot ?? null
  const state = context ? controller!.runtimeReadState(api.baseUrl, context) : undefined
  const snapshotCurrent = Boolean(resource && query.status !== 'error' && (!state || query.resource?.epoch === state.epoch &&
    (resource.observationVersion == null ? state.floor === 0 : resource.observationVersion >= state.floor)))
  const refresh = query.refresh
  const refreshSnapshot = useCallback(async () => (await refresh())?.snapshot ?? null, [refresh])
  return { ...query, resource, snapshotCurrent, refresh: refreshSnapshot }
}
export function useRuntimeObservation(identity: Identity) {
  const { placement } = identity
  return useSnapshot(['health', placement.deploymentId, placement.deploymentRevision ?? 1, placement.desiredGeneration], identity, fetchResourceObservation, false)
}
export function useRuntimeDiagnostics(identity: Identity) {
  const { placement, resourceType, resourceId } = identity
  return useSnapshot(['runtime-discovery', placement.deploymentId, resourceType, resourceId, placement.deploymentTargetId, placement.deploymentRevision ?? 1, placement.desiredGeneration], identity, fetchResourceDiagnostics, true)
}
