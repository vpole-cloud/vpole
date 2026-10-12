import { ApiError, buildHeaders, fetchJson, request } from '../../api/transport'
import type { ResourcePlacement, ResourceEndpointDiscovery, ResourceDeploymentOperationsPage, ResourceDeploymentObservation, ResourceDeploymentDiagnostics } from './types'

export function fetchResourceDeploymentSet(base: string, resourceType: string, resourceId: string, token?: string, tenantId?: string, signal?: AbortSignal) {
  return fetchJson<ResourcePlacement[]>(`${base}/api/v1/resource-deployments/?${new URLSearchParams({ resourceType, resourceId })}`, { headers: buildHeaders(token, tenantId), signal })
}

export async function fetchResourceEndpoints(base: string, resourceType: string, resourceId: string, token?: string, tenantId?: string, signal?: AbortSignal,
  source?: { resourceType: string; resourceId: string }) {
  const query = new URLSearchParams({ resourceType, resourceId })
  if (source) { query.set('sourceResourceType', source.resourceType); query.set('sourceResourceId', source.resourceId) }
  let response: ResourceEndpointDiscovery
  try { response = await fetchJson<ResourceEndpointDiscovery>(`${base}/api/v1/resource-connections/endpoints?${query}`, { headers: buildHeaders(token, tenantId), signal }) }
  catch (error) {
    if (signal?.aborted) throw new DOMException('Aborted', 'AbortError')
    throw new ApiError('Endpoint discovery is unavailable. Refresh to retry.', error instanceof ApiError ? error.status : 0)
  }
  if (!response || !['Supported', 'Unsupported'].includes(response.availability) || !Array.isArray(response.endpoints) || !response.topology || !['NotEvaluated', 'Eligible', 'Ineligible'].includes(response.topology.status)) throw new ApiError('Endpoint discovery response is unavailable.', 502)
  return response
}

export async function retryResourceDeployment(apiBaseUrl: string, deploymentId: string, expectedRevision: number, expectedOperation: string, token?: string, tenantId?: string, signal?: AbortSignal) {
  return healthRequest<unknown>(apiBaseUrl, `/${encodeURIComponent(deploymentId)}/retry?${new URLSearchParams({ expectedRevision: String(expectedRevision), expectedOperation })}`, token, tenantId, signal, 'POST')
}

async function healthRequest<T>(base: string, path: string, token?: string, tenantId?: string, signal?: AbortSignal, method = 'GET'): Promise<T> {
  const response = await request(`${base}/api/v1/resource-deployments${path}`, { method, headers: buildHeaders(token, tenantId), signal }).catch(() => {
    if (signal?.aborted) throw new DOMException('Aborted', 'AbortError')
    throw new ApiError('Resource state is unavailable. Refresh to reconnect.', 0)
  })
  if (!response.ok) throw new ApiError(response.status === 409 ? 'This operation changed or is no longer retryable. Refresh before choosing another attempt.'
    : response.status === 403 ? 'Your role cannot perform this action.' : response.status === 404 ? 'This deployment is no longer available in the active tenant.'
    : 'Resource state is unavailable. Refresh to reconnect.', response.status)
  try { return await response.json() as T } catch { throw new ApiError('Resource state response is unavailable. Refresh to reconnect.', 502) }
}

export interface ResourceOperationsFilter { resourceGroupId?: string; resourceType?: string; resourceId?: string }

export function fetchResourceOperations(base: string, filter: ResourceOperationsFilter, page: number, token?: string, tenantId?: string, signal?: AbortSignal) {
  const query = new URLSearchParams({ pageNumber: String(page), pageSize: '10' })
  for (const [key, value] of Object.entries(filter)) if (value) query.set(key, value)
  return healthRequest<ResourceDeploymentOperationsPage>(base, `/operations?${query}`, token, tenantId, signal)
}

export function fetchResourceObservation(base: string, deploymentId: string, token?: string, tenantId?: string, signal?: AbortSignal) {
  return healthRequest<ResourceDeploymentObservation>(base, `/${encodeURIComponent(deploymentId)}/observation`, token, tenantId, signal)
}

export function fetchResourceDiagnostics(base: string, deploymentId: string, token?: string, tenantId?: string, signal?: AbortSignal) {
  return healthRequest<ResourceDeploymentDiagnostics>(base, `/${encodeURIComponent(deploymentId)}/diagnostics`, token, tenantId, signal)
}
