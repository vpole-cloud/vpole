import { ApiError, buildHeaders, request } from '../../api/transport'
import type { PaginatedResponse, WebServiceDeployment } from '../../types'
import type { TenantDomainZone, DomainZoneBindingUsage, HostnameBinding } from './types'

export async function fetchDomainZones(
  apiBaseUrl: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return domainRequest<TenantDomainZone[]>(apiBaseUrl, '/domain-zones', token, tenantId, signal)
}

const domainProblems: Record<string, string> = {
  'domains.hostname.invalid-name': 'The hostname is invalid or too long for this zone.',
  'domains.hostname.git-owned': 'This hostname binding is managed by GitOps.',
  'domains.zone.git-owned': 'This reservation is managed by GitOps. Change its managing source.',
  'domains.zone.name-conflict': 'This technical name is already used in the active tenant.',
  'domains.hostname.name-conflict':
    'This hostname binding technical name is already used in the active tenant.',
  'domains.zone.not-found': 'This zone is unavailable in the active tenant. Refresh the catalog.',
  'domains.zone.already-reserved':
    'This zone label is already reserved on the target. Choose another label.',
  'domains.zone.delete.generated':
    'Generated zones are maintained by deployment ownership and cannot be removed here.',
  'domains.zone.delete.has-hostname-bindings':
    'Detach the zone’s hostname aliases from their owning resources before removing the reservation.',
  'domains.zone.delete.conflict': 'The zone changed. Refresh its ownership before trying again.',
  'domains.zone.delete.reference-removal-rejected':
    'The target could not release this reservation. Refresh and retry.',
  'domains.hostname.already-bound':
    'This hostname is already reserved in the configured routing scope. Choose another name. Other owners are not disclosed.',
  'domains.hostname.zone-target-mismatch': 'Choose a zone on this deployment target.',
  'domains.hostname.zone-not-found':
    'The selected zone is unavailable in this tenant. Refresh zones.',
  'domains.hostname.zone-unavailable':
    'The selected zone is unavailable. Refresh zones before retrying.',
  'domains.hostname.zone-changed':
    'The zone changed during the update. Refresh hostnames and retry.',
  'domains.hostname.not-found':
    'This alias has already been removed or is unavailable. Refresh hostnames.',
  'domains.hostname.delete.generated':
    'Generated hostnames are removed with their deployment. Detach only custom aliases here.',
  'domains.hostname.delete.conflict': 'The alias changed. Refresh hostnames before retrying.',
  'web-services.private-hostname-unavailable':
    'Enable public exposure in Configuration before attaching a hostname.',
  'deployment-targets.public-ingress-unavailable':
    'This target needs a configured ingress class, issuer and public base domain. Ask the platform administrator to configure public ingress.',
  'deployment-targets.workloads-unavailable':
    'This target is unavailable for new workloads. Refresh targets.',
}

export class DomainApiError extends ApiError {
  readonly code: string
  constructor(status: number, code = '') {
    super(
      domainProblems[code] ??
        (status === 403
          ? 'Your tenant role cannot perform this domain action.'
          : status === 400
            ? 'Check the zone and hostname labels. Use valid DNS labels.'
            : 'The domain request failed. Refresh ownership and target state before retrying.'),
      status,
    )
    this.code = code
  }
}

async function domainRequest<T>(
  base: string,
  path: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
  method = 'GET',
  body?: unknown,
): Promise<T> {
  let response: Response
  try {
    response = await request(`${base}/api/v1${path}`, {
      method,
      headers: {
        ...buildHeaders(token, tenantId),
        ...(body ? { 'Content-Type': 'application/json' } : {}),
      },
      signal,
      cache: 'no-store',
      ...(body ? { body: JSON.stringify(body) } : {}),
    })
  } catch {
    if (signal?.aborted) throw new DOMException('Request cancelled', 'AbortError')
    throw new DomainApiError(0)
  }
  if (!response.ok) {
    let code = ''
    try {
      const problem: unknown = await response.json()
      if (
        problem &&
        typeof problem === 'object' &&
        'code' in problem &&
        typeof problem.code === 'string' &&
        Object.hasOwn(domainProblems, problem.code)
      )
        code = problem.code
    } catch {
      /* No arbitrary server metadata in conflicts. */
    }
    throw new DomainApiError(response.status, code)
  }
  if (response.status === 204) return undefined as T
  try {
    return (await response.json()) as T
  } catch {
    throw new DomainApiError(response.status)
  }
}

export const fetchDomainZone = (
  base: string,
  id: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) =>
  domainRequest<TenantDomainZone>(
    base,
    `/domain-zones/${encodeURIComponent(id)}`,
    token,
    tenantId,
    signal,
  )
export const fetchDomainZoneBindings = (
  base: string,
  id: string,
  page: number,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) =>
  domainRequest<PaginatedResponse<DomainZoneBindingUsage>>(
    base,
    `/domain-zones/${encodeURIComponent(id)}/bindings?pageNumber=${page}&pageSize=25`,
    token,
    tenantId,
    signal,
  )

export async function createDomainZone(
  apiBaseUrl: string,
  input: { deploymentTargetId: string; label: string; technicalName: string },
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return domainRequest<TenantDomainZone>(
    apiBaseUrl,
    '/domain-zones',
    token,
    tenantId,
    signal,
    'POST',
    input,
  )
}

export async function deleteDomainZone(
  apiBaseUrl: string,
  id: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return domainRequest<void>(
    apiBaseUrl,
    `/domain-zones/${encodeURIComponent(id)}`,
    token,
    tenantId,
    signal,
    'DELETE',
  )
}

export async function fetchWebServiceHostnameBindings(
  apiBaseUrl: string,
  id: string,
  deploymentId: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return domainRequest<HostnameBinding[]>(
    apiBaseUrl,
    `/web-services/${encodeURIComponent(id)}/deployments/${encodeURIComponent(deploymentId)}/hostnames`,
    token,
    tenantId,
    signal,
  )
}

export async function addWebServiceHostnameBinding(
  apiBaseUrl: string,
  id: string,
  deploymentId: string,
  input: { tenantDomainZoneId: string; relativeName: string; technicalName: string },
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return domainRequest<WebServiceDeployment>(
    apiBaseUrl,
    `/web-services/${encodeURIComponent(id)}/deployments/${encodeURIComponent(deploymentId)}/hostnames`,
    token,
    tenantId,
    signal,
    'POST',
    input,
  )
}

export async function deleteWebServiceHostnameBinding(
  apiBaseUrl: string,
  id: string,
  deploymentId: string,
  bindingId: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return domainRequest<WebServiceDeployment>(
    apiBaseUrl,
    `/web-services/${encodeURIComponent(id)}/deployments/${encodeURIComponent(deploymentId)}/hostnames/${encodeURIComponent(bindingId)}`,
    token,
    tenantId,
    signal,
    'DELETE',
  )
}
