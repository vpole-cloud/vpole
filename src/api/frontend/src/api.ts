export { fetchResourceDeploymentSet, fetchResourceEndpoints, retryResourceDeployment, fetchResourceOperations, fetchResourceObservation, fetchResourceDiagnostics } from './modules/operations/api'
export type { ResourceOperationsFilter } from './modules/operations/api'
export { fetchStorageProfiles, fetchPlatformDeploymentTargets, fetchKubernetesStorageClasses, fetchKubernetesPersistentVolumes, fetchKubernetesPersistentVolumeClaims, createStorageProfile, updateStorageProfile } from './modules/platform/storageApi'
export { fetchGitProviderConnections, beginGitHubInstallation, disconnectGitProviderConnection, fetchProviderRepositories, fetchProviderBranches, analyzeProviderRepository, linkProviderRepository, fetchSourceRepositories, reanalyzeSourceRepository, unlinkSourceRepository } from './modules/platform/gitApi'
export { fetchProtectedGreeting, searchResources } from './modules/platform/discoveryApi'
export { SecretApiError, fetchSecrets, fetchSecret, createSecret, rotateSecret, deleteSecret, fetchSecretUsage, fetchSecretProjections, bindSecret, detachSecret } from './modules/secrets/api'
export { fetchDomainZones, DomainApiError, fetchDomainZone, fetchDomainZoneBindings, createDomainZone, deleteDomainZone, fetchWebServiceHostnameBindings, addWebServiceHostnameBinding, deleteWebServiceHostnameBinding } from './modules/domains/api'
export { fetchIsolationScopes, fetchResourceGroups, fetchResourceGroup, fetchResourceGroupResources, createResourceGroup, updateResourceGroup, deleteResourceGroup } from './modules/resource-groups/api'
export { fetchRedisServices, fetchRedisService, createRedisService, updateRedisService, deleteRedisService } from './modules/redis/api'
export { fetchPostgresClusters, fetchPostgresCluster, fetchPostgresCapabilities, createPostgresCluster, updatePostgresCluster, deletePostgresCluster, fetchPostgresConnectionString, rotatePostgresPassword } from './modules/postgres/api'
import { consumeRuntimeLogFrames, runtimeReasons } from './api/runtimeLogs'
import { buildHeaders, fetchJson, request, sendJson } from './api/transport'
export { ApiError, validationMessage } from './api/transport'
export { fetchTenants, normalizeTenantRole } from './api/tenants'
import type {
  AvailableComputeProfile,
  AvailableDeploymentTarget,
  PaginatedResponse,
  ResourceConnection,
  PoleResourceType,
  StorageProfile,
} from './types'

export async function fetchAvailableStorageProfiles(
  apiBaseUrl: string,
  resourceType: PoleResourceType,
  deploymentTargetId: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  const query = new URLSearchParams({ resourceType, deploymentTargetId })

  return fetchJson<StorageProfile[]>(
    `${apiBaseUrl}/api/v1/storage-profiles?${query.toString()}`,
    {
      headers: buildHeaders(token, tenantId),
      signal,
    },
  )
}

export function fetchResourceConnections(base: string, sourceResourceId: string, pageNumber: number, token?: string, tenantId?: string, signal?: AbortSignal) {
  const query = new URLSearchParams({ sourceResourceType: 'pole/web-service', sourceResourceId, pageNumber: String(pageNumber), pageSize: '100' })
  return fetchJson<PaginatedResponse<ResourceConnection>>(`${base}/api/v1/resource-connections/?${query}`, { headers: buildHeaders(token, tenantId), signal })
}
export function createResourceConnection(base: string, input: { name: string; sourceResourceType: string; sourceResourceId: string; destinationResourceType: string; destinationResourceId: string; endpointId: string }, token?: string, tenantId?: string, signal?: AbortSignal) {
  return sendJson<{ id: string; name: string; generation: number; status: string }>(`${base}/api/v1/resource-connections/`, { method: 'POST', headers: buildHeaders(token, tenantId), body: JSON.stringify(input), signal })
}
export function deleteResourceConnection(base: string, id: string, token?: string, tenantId?: string, signal?: AbortSignal) {
  return sendJson<{ id: string; generation: number; status: string }>(`${base}/api/v1/resource-connections/${encodeURIComponent(id)}`, { method: 'DELETE', headers: buildHeaders(token, tenantId), signal })
}

const webServiceResourceType = 'pole/web-service'

export async function fetchAvailableDeploymentTargets(
  apiBaseUrl: string,
  isolationScopeId: string | undefined,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
  requiresPublicIngress = true,
  resourceType = webServiceResourceType,
) {
  const query = new URLSearchParams({
    resourceType,
    requiresPublicIngress: String(requiresPublicIngress),
  })
  if (isolationScopeId) {
    query.set('isolationScopeId', isolationScopeId)
  }

  return fetchJson<AvailableDeploymentTarget[]>(
    `${apiBaseUrl}/api/v1/deployment-targets/available?${query.toString()}`,
    { headers: buildHeaders(token, tenantId), signal },
  )
}

export async function fetchAvailableComputeProfiles(
  apiBaseUrl: string,
  deploymentTargetId: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
  resourceType = webServiceResourceType,
) {
  const query = new URLSearchParams({
    deploymentTargetId,
    resourceType,
  })
  return fetchJson<AvailableComputeProfile[]>(
    `${apiBaseUrl}/api/v1/compute-profiles/available?${query.toString()}`,
    { headers: buildHeaders(token, tenantId), signal },
  )
}
export class RuntimeLogUnavailableError extends Error {
  readonly reason: string
  constructor(reason: string) { super('Runtime logs are unavailable. Refresh instances and start a new recent window.'); this.reason = reason }
}
export async function readResourceRuntimeLogs(base: string, deploymentId: string, selection: import('./types').RuntimeLogSelection,
  onFrame: (frame: import('./types').RuntimeLogFrame) => void, token?: string, tenantId?: string, signal?: AbortSignal) {
  const query = new URLSearchParams(Object.entries(selection).map(([key, value]) => [key, String(value)]))
  const response = await request(`${base}/api/v1/resource-deployments/${encodeURIComponent(deploymentId)}/runtime-logs?${query}`, { headers: buildHeaders(token, tenantId), signal }).catch(() => {
    if (signal?.aborted) throw new DOMException('Aborted', 'AbortError')
    throw new Error('Runtime logs could not be reached. Reconnect with a new recent window.')
  })
  if (signal?.aborted) { await response.body?.cancel().catch(() => {}); throw new DOMException('Aborted', 'AbortError') }
  if (!response.ok) {
    if (response.status === 409) {
      let reason = 'LogsUnavailable'
      try { const body = await response.json() as { reason?: string }; if (body.reason && runtimeReasons.has(body.reason)) reason = body.reason } catch { /* Keep fixed error text. */ }
      throw new RuntimeLogUnavailableError(reason)
    }
    throw new Error(response.status === 403 ? 'Your tenant role cannot read these logs.' : response.status === 404 ? 'This deployment is unavailable in the active tenant.'
      : response.status === 400 ? 'The instance selection changed or is invalid. Refresh instances.' : 'Runtime logs could not be reached. Reconnect with a new recent window.')
  }
  if (!response.headers.get('Content-Type')?.startsWith('application/x-ndjson') || !response.body) throw new Error('The runtime log response is unavailable. Reconnect with a new recent window.')
  return consumeRuntimeLogFrames(response.body, onFrame, signal)
}

export { updateWebServiceDependencies, fetchWebServices, fetchWebService, createWebService, updateWebService, fetchWebServiceDeployments, addWebServiceDeployment, updateWebServiceDeployment, deleteWebServiceDeployment, retryWebServiceDeployment, fetchWebServiceReleases, redeployWebService, rollbackWebService, deleteWebService } from './modules/web-services/api'
export { fetchSourceBuildJob, fetchSourceBuildLogs, fetchSourceBuildConfigurations, createSourceBuildConfiguration, deleteSourceBuildConfiguration, updateSourceBuildRecipe, startSourceBuild, fetchSourceBuildJobs } from './modules/web-services/buildApi'
