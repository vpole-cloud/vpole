import { buildHeaders, fetchJson } from './transport'
import type {
  AvailableComputeProfile,
  AvailableDeploymentTarget,
  StorageProfile,
  PoleResourceType,
} from '../types'

export async function fetchAvailableStorageProfiles(
  apiBaseUrl: string,
  resourceType: PoleResourceType,
  deploymentTargetId: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  const query = new URLSearchParams({ resourceType, deploymentTargetId })

  return fetchJson<StorageProfile[]>(`${apiBaseUrl}/api/v1/storage-profiles?${query.toString()}`, {
    headers: buildHeaders(token, tenantId),
    signal,
  })
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
