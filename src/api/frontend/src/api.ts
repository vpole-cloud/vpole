export { fetchStorageProfiles, fetchPlatformDeploymentTargets, fetchKubernetesStorageClasses, fetchKubernetesPersistentVolumes, fetchKubernetesPersistentVolumeClaims, createStorageProfile, updateStorageProfile } from './modules/platform/storageApi'
export { fetchGitProviderConnections, beginGitHubInstallation, disconnectGitProviderConnection, fetchProviderRepositories, fetchProviderBranches, analyzeProviderRepository, linkProviderRepository, fetchSourceRepositories, reanalyzeSourceRepository, unlinkSourceRepository } from './modules/platform/gitApi'
export { fetchProtectedGreeting, searchResources } from './modules/platform/discoveryApi'
export { SecretApiError, fetchSecrets, fetchSecret, createSecret, rotateSecret, deleteSecret, fetchSecretUsage, fetchSecretProjections, bindSecret, detachSecret } from './modules/secrets/api'
export { fetchDomainZones, DomainApiError, fetchDomainZone, fetchDomainZoneBindings, createDomainZone, deleteDomainZone, fetchWebServiceHostnameBindings, addWebServiceHostnameBinding, deleteWebServiceHostnameBinding } from './modules/domains/api'
export { fetchIsolationScopes, fetchResourceGroups, fetchResourceGroup, fetchResourceGroupResources, createResourceGroup, updateResourceGroup, deleteResourceGroup } from './modules/resource-groups/api'
export { fetchRedisServices, fetchRedisService, createRedisService, updateRedisService, deleteRedisService } from './modules/redis/api'
export { fetchPostgresClusters, fetchPostgresCluster, fetchPostgresCapabilities, createPostgresCluster, updatePostgresCluster, deletePostgresCluster, fetchPostgresConnectionString, rotatePostgresPassword } from './modules/postgres/api'
import { consumeRuntimeLogFrames, runtimeReasons } from './api/runtimeLogs'
import { ApiError, buildHeaders, fetchJson, request, sendJson } from './api/transport'
export { ApiError, validationMessage } from './api/transport'
export { fetchTenants, normalizeTenantRole } from './api/tenants'
import type {
  AvailableComputeProfile,
  AvailableDeploymentTarget,
  PaginatedResponse,
  ResourceConnection,
  PoleResourceType,
  ResourcePlacement,
  ResourceEndpointDiscovery,
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
export function fetchResourceDeploymentSet(base: string, resourceType: string, resourceId: string, token?: string, tenantId?: string, signal?: AbortSignal) {
  return fetchJson<ResourcePlacement[]>(`${base}/api/v1/resource-deployments/?${new URLSearchParams({ resourceType, resourceId })}`, { headers: buildHeaders(token, tenantId), signal })
}

export async function fetchResourceEndpoints(base: string, resourceType: string, resourceId: string, token?: string, tenantId?: string, signal?: AbortSignal,
  source?: { resourceType: string; resourceId: string }) {
  const query = new URLSearchParams({ resourceType, resourceId })
  if (source) { query.set('sourceResourceType', source.resourceType); query.set('sourceResourceId', source.resourceId) }
  const response = await fetchJson<ResourceEndpointDiscovery>(`${base}/api/v1/resource-connections/endpoints?${query}`, { headers: buildHeaders(token, tenantId), signal })
  if (!response || !['Supported', 'Unsupported'].includes(response.availability) || !Array.isArray(response.endpoints) || !response.topology || !['NotEvaluated', 'Eligible', 'Ineligible'].includes(response.topology.status)) throw new ApiError('Endpoint discovery response is unavailable.', 502)
  return response
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

export async function retryResourceDeployment(apiBaseUrl: string, deploymentId: string, expectedRevision: number, expectedOperation: string, token?: string, tenantId?: string, signal?: AbortSignal) {
  return healthRequest<unknown>(apiBaseUrl, `/${encodeURIComponent(deploymentId)}/retry?${new URLSearchParams({ expectedRevision: String(expectedRevision), expectedOperation })}`, token, tenantId, signal, 'POST')
}

async function healthRequest<T>(base: string, path: string, token?: string, tenantId?: string, signal?: AbortSignal, method = 'GET'): Promise<T> {
  const response = await request(`${base}/api/v1/resource-deployments${path}`, { method, headers: buildHeaders(token, tenantId), signal })
  if (!response.ok) throw new Error(response.status === 409 ? 'This operation changed or is no longer retryable. Refresh before choosing another attempt.'
    : response.status === 403 ? 'Your role cannot perform this action.' : response.status === 404 ? 'This deployment is no longer available in the active tenant.'
    : 'Resource state is unavailable. Refresh to reconnect.')
  return response.json() as Promise<T>
}

export interface ResourceOperationsFilter { resourceGroupId?: string; resourceType?: string; resourceId?: string }
export function fetchResourceOperations(base: string, filter: ResourceOperationsFilter, page: number, token?: string, tenantId?: string, signal?: AbortSignal) {
  const query = new URLSearchParams({ pageNumber: String(page), pageSize: '10' })
  for (const [key, value] of Object.entries(filter)) if (value) query.set(key, value)
  return healthRequest<import('./types').ResourceDeploymentOperationsPage>(base, `/operations?${query}`, token, tenantId, signal)
}
export function fetchResourceObservation(base: string, deploymentId: string, token?: string, tenantId?: string, signal?: AbortSignal) {
  return healthRequest<import('./types').ResourceDeploymentObservation>(base, `/${encodeURIComponent(deploymentId)}/observation`, token, tenantId, signal)
}

export function fetchResourceDiagnostics(base: string, deploymentId: string, token?: string, tenantId?: string, signal?: AbortSignal) {
  return healthRequest<import('./types').ResourceDeploymentDiagnostics>(base, `/${encodeURIComponent(deploymentId)}/diagnostics`, token, tenantId, signal)
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
