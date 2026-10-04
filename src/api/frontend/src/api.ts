import type {
  BeginGitHubInstallationResponse,
  AvailableComputeProfile,
  AvailableDeploymentTarget,
  GitProviderConnection,
  GitProviderPage,
  KubernetesPersistentVolume,
  KubernetesPersistentVolumeClaim,
  KubernetesStorageClass,
  PaginatedResponse,
  ResourceGroup,
  IsolationScope,
  ResourceRegistration,
  ResourceConnection,
  WebServiceSpec,
  ResourceGroupInput,
  PoleResourceType,
  StorageProfile,
  StorageProfileInput,
  ProviderBranch,
  ProviderRepository,
  RepositoryAnalysis,
  SourceRepository,
  SourceBuildConfiguration,
  SourceBuildJob,
  SourceBuildRecipe,
  WebServiceInput,
  WebService,
  Tenant,
  TenantRole,
  TenantDomainZone,
  DomainZoneBindingUsage,
  HostnameBinding,
  WebServiceDeployment,
  WebServiceDeploymentInput,
  WebServiceDeploymentUpdateInput,
  WebServiceReleaseHistory,
  SearchHit,
  PostgresCluster,
  PostgresCapabilities,
  PostgresInput,
  PostgresUpdateInput,
  RedisService,
  RedisInput,
  RedisUpdateInput,
  ResourcePlacement,
  SecretMetadata,
  SecretInput,
  SecretBindingInput,
  SecretUsage,
  SecretProjection,
  ResourceEndpointDiscovery,
} from './types'

type TenantResponse = Omit<Tenant, 'role'> & {
  role: TenantRole | number
}

const tenantRoles: Record<number, TenantRole> = {
  0: 'Viewer',
  1: 'Member',
  2: 'Admin',
  3: 'Owner',
}

export function normalizeTenantRole(role: TenantRole | number): TenantRole {
  if (typeof role === 'string') return role

  const normalized = tenantRoles[role]
  if (normalized) return normalized

  throw new Error(`Unknown tenant role value: ${role}`)
}

const buildHeaders = (token?: string, tenantId?: string) => {
  const headers: Record<string, string> = {
    Accept: 'application/json',
  }

  if (token) {
    headers.Authorization = `Bearer ${token}`
  }

  if (tenantId) {
    headers['X-Pole-Tenant-ID'] = tenantId
  }

  return headers
}

export async function fetchIsolationScopes(
  apiBaseUrl: string,
  resourceGroupId: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return fetchJson<IsolationScope[]>(`${apiBaseUrl}/api/v1/resource-groups/${encodeURIComponent(resourceGroupId)}/isolation-scopes`, {
    headers: buildHeaders(token, tenantId),
    signal,
  })
}

export async function fetchTenants(
  apiBaseUrl: string,
  token?: string,
  signal?: AbortSignal,
) {
  const tenants = await fetchJson<TenantResponse[]>(`${apiBaseUrl}/api/v1/tenants`, {
    headers: buildHeaders(token),
    signal,
  })

  return tenants.map((tenant) => ({
    ...tenant,
    role: normalizeTenantRole(tenant.role),
  }))
}

export async function fetchStorageProfiles(
  apiBaseUrl: string,
  token?: string,
  signal?: AbortSignal,
) {
  return fetchJson<StorageProfile[]>(
    `${apiBaseUrl}/api/v1/platform/storage/profiles`,
    {
      headers: buildHeaders(token),
      signal,
    },
  )
}

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

export async function fetchKubernetesStorageClasses(
  apiBaseUrl: string,
  token?: string,
  signal?: AbortSignal,
) {
  return fetchJson<KubernetesStorageClass[]>(
    `${apiBaseUrl}/api/v1/platform/storage/storage-classes`,
    {
      headers: buildHeaders(token),
      signal,
    },
  )
}

export async function fetchKubernetesPersistentVolumes(
  apiBaseUrl: string,
  token?: string,
  signal?: AbortSignal,
) {
  return fetchJson<KubernetesPersistentVolume[]>(
    `${apiBaseUrl}/api/v1/platform/storage/persistent-volumes`,
    {
      headers: buildHeaders(token),
      signal,
    },
  )
}

export async function fetchKubernetesPersistentVolumeClaims(
  apiBaseUrl: string,
  token?: string,
  signal?: AbortSignal,
) {
  return fetchJson<KubernetesPersistentVolumeClaim[]>(
    `${apiBaseUrl}/api/v1/platform/storage/persistent-volume-claims`,
    {
      headers: buildHeaders(token),
      signal,
    },
  )
}

export class ApiError extends Error {
  status: number
  fieldErrors: Record<string, string[]>
  constructor(message: string, status: number, fieldErrors: Record<string, string[]> = {}) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.fieldErrors = fieldErrors
  }
}

const secretProblems: Record<string, string> = {
  'secrets.managed-by-provider': 'Change this managed credential through its owning resource.',
  'secrets.not-found': 'Secret not found in this tenant.',
  'secrets.name-already-exists': 'This Secret name already exists in the tenant.',
  'secrets.reference-rejected': 'The selected resource or deployment is unavailable in this group and isolation scope. Refresh the choices.',
  'secrets.encryption-unavailable': 'Secret encryption is temporarily unavailable.',
  'secrets.version-conflict': 'The Secret version changed. Refresh metadata and review the complete replacement before rotating again.',
  'secrets.mapped-keys-required': 'Keep every source key used by an existing binding in the replacement.',
  'secrets.binding-limit-reached': 'This Secret has reached its binding limit.',
  'secrets.consumer-already-bound': 'This resource already has a binding to the Secret.',
  'secrets.unknown-keys': 'A mapping uses an unknown Secret key.',
  'secrets.bindings-remain': 'Detach all bindings before deleting this Secret.',
}

export class SecretApiError extends ApiError {
  readonly code: string
  constructor(status: number, code: string) {
    super(secretProblems[code] ?? (status === 403 ? 'You do not have permission to change this Secret.' :
      status === 400 ? 'Check the metadata and complete key/value map.' : 'The Secret request failed. Refresh metadata before retrying.'), status)
    this.name = 'SecretApiError'
    this.code = code
  }
}

// Never expose arbitrary response text, validation messages or request values on a Secret error.
async function secretRequest<T>(base: string, path: string, token?: string, tenantId?: string, signal?: AbortSignal, method = 'GET', body?: unknown): Promise<T> {
  let response: Response
  try {
    response = await fetch(`${base}/api/v1/secrets${path}`, { method, headers: { ...buildHeaders(token, tenantId), ...(body ? { 'Content-Type': 'application/json' } : {}) },
      cache: 'no-store', signal, ...(body ? { body: JSON.stringify(body) } : {}) })
  } catch {
    if (signal?.aborted) throw new DOMException('Request cancelled', 'AbortError')
    throw new SecretApiError(0, '')
  }
  if (!response.ok) {
    let code = ''
    try {
      const problem: unknown = await response.json()
      if (problem && typeof problem === 'object' && 'code' in problem && typeof problem.code === 'string' && Object.hasOwn(secretProblems, problem.code)) code = problem.code
    } catch { /* Body contents must never escape this transport. */ }
    throw new SecretApiError(response.status, code)
  }
  if (response.status === 204) return undefined as T
  try { return await response.json() as T } catch { throw new SecretApiError(response.status, '') }
}

export function fetchSecrets(base: string, after: string | undefined, token?: string, tenantId?: string, signal?: AbortSignal) {
  const query = new URLSearchParams({ pageSize: '50' }); if (after) query.set('after', after)
  return secretRequest<{ items: SecretMetadata[]; nextCursor: string | null }>(base, `/?${query}`, token, tenantId, signal)
}
export const fetchSecret = (base: string, id: string, token?: string, tenantId?: string, signal?: AbortSignal) => secretRequest<SecretMetadata>(base, `/${encodeURIComponent(id)}`, token, tenantId, signal)
export const createSecret = (base: string, input: SecretInput, token?: string, tenantId?: string, signal?: AbortSignal) => secretRequest<SecretMetadata>(base, '/', token, tenantId, signal, 'POST', input)
export const rotateSecret = (base: string, id: string, expectedVersion: number, values: Record<string, string>, token?: string, tenantId?: string, signal?: AbortSignal) => secretRequest<SecretMetadata>(base, `/${encodeURIComponent(id)}/versions`, token, tenantId, signal, 'POST', { expectedVersion, values })
export const deleteSecret = (base: string, id: string, token?: string, tenantId?: string, signal?: AbortSignal) => secretRequest<void>(base, `/${encodeURIComponent(id)}`, token, tenantId, signal, 'DELETE')
export const fetchSecretUsage = (base: string, id: string, token?: string, tenantId?: string, signal?: AbortSignal) => secretRequest<SecretUsage[]>(base, `/${encodeURIComponent(id)}/usage`, token, tenantId, signal)
export const fetchSecretProjections = (base: string, id: string, token?: string, tenantId?: string, signal?: AbortSignal) => secretRequest<SecretProjection[]>(base, `/${encodeURIComponent(id)}/projections`, token, tenantId, signal)
export const bindSecret = (base: string, id: string, input: SecretBindingInput, token?: string, tenantId?: string, signal?: AbortSignal) => secretRequest<SecretMetadata>(base, `/${encodeURIComponent(id)}/bindings`, token, tenantId, signal, 'POST', input)
export const detachSecret = (base: string, id: string, bindingId: string, token?: string, tenantId?: string, signal?: AbortSignal) => secretRequest<SecretMetadata>(base, `/${encodeURIComponent(id)}/bindings/${encodeURIComponent(bindingId)}`, token, tenantId, signal, 'DELETE')
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
export function updateWebServiceDependencies(base: string, service: WebService, config: WebServiceSpec['config'], token?: string, tenantId?: string, signal?: AbortSignal) {
  return sendJson<{ id: string; resourceRevision: number }>(`${base}/api/v1/web-services/${encodeURIComponent(service.id)}`, {
    method: 'PUT', headers: buildHeaders(token, tenantId), signal,
    body: JSON.stringify({ spec: { ...service.spec, config }, exposure: service.exposure.mode, tags: service.tags ?? [], expectedRevision: service.activeRevision }),
  })
}

export function validationMessage(error: unknown, field: string): string {
  if (!(error instanceof ApiError)) return ''
  return Object.entries(error.fieldErrors).filter(([path]) => {
    const normalized = path.replace(/^body\./i, '').replace(/\[\d+\]/g, '').toLowerCase()
    return normalized === field.toLowerCase() || normalized.startsWith(`${field.toLowerCase()}.`)
  }).flatMap(([, messages]) => messages).join(' ')
}

async function responseError(response: Response): Promise<ApiError> {
  const text = await response.text()
  const fallback = text || `Request failed with status ${response.status}`
  try {
    const problem: unknown = JSON.parse(text)
    if (!problem || typeof problem !== 'object') return new ApiError(fallback, response.status)
    const value = problem as { detail?: unknown; title?: unknown; errors?: unknown }
    const fields: Record<string, string[]> = {}
    if (value.errors && typeof value.errors === 'object') {
      for (const [key, messages] of Object.entries(value.errors)) {
        if (Array.isArray(messages)) fields[key] = messages.filter((message): message is string => typeof message === 'string')
      }
    }
    const description = typeof value.detail === 'string' ? value.detail : typeof value.title === 'string' ? value.title : ''
    const messages = Object.values(fields).flat().join(' ')
    return new ApiError([description, messages].filter(Boolean).join(' ') || fallback, response.status, fields)
  } catch { return new ApiError(fallback, response.status) }
}

async function fetchJson<T>(
  input: RequestInfo | URL,
  init?: RequestInit,
): Promise<T> {
  const response = await fetch(input, init)

  if (!response.ok) {
    throw await responseError(response)
  }

  return (await response.json()) as T
}

async function sendJson<T>(
  input: RequestInfo | URL,
  init: RequestInit,
): Promise<T> {
  return fetchJson<T>(input, {
    ...init,
    headers: {
      'Content-Type': 'application/json',
      ...(init.headers ?? {}),
    },
  })
}

export async function fetchResourceGroups(
  apiBaseUrl: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
  inventory: { pageNumber?: number; pageSize?: number; search?: string } = {},
) {
  const query = new URLSearchParams({ pageNumber: String(inventory.pageNumber ?? 1), pageSize: String(inventory.pageSize ?? 6) })
  if (inventory.search?.trim()) query.set('search', inventory.search.trim())
  return fetchJson<PaginatedResponse<ResourceGroup>>(
    `${apiBaseUrl}/api/v1/resource-groups?${query}`,
    {
      headers: buildHeaders(token, tenantId),
      signal,
    },
  )
}

export async function fetchResourceGroup(apiBaseUrl: string, id: string, token?: string, tenantId?: string, signal?: AbortSignal) {
  return fetchJson<ResourceGroup>(`${apiBaseUrl}/api/v1/resource-groups/${encodeURIComponent(id)}`, {
    headers: buildHeaders(token, tenantId), signal,
  })
}

export async function fetchResourceGroupResources(
  apiBaseUrl: string,
  resourceGroupId: string,
  pageNumber = 1,
  pageSize = 20,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  const query = new URLSearchParams({
    pageNumber: pageNumber.toString(),
    pageSize: pageSize.toString(),
  })

  return fetchJson<PaginatedResponse<ResourceRegistration>>(
    `${apiBaseUrl}/api/v1/resource-groups/${encodeURIComponent(resourceGroupId)}/resources?${query.toString()}`,
    {
      headers: buildHeaders(token, tenantId),
      signal,
    },
  )
}

export async function fetchWebServices(
  apiBaseUrl: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
  inventory: { pageNumber?: number; pageSize?: number; search?: string } = {},
) {
  const query = new URLSearchParams({ pageNumber: String(inventory.pageNumber ?? 1), pageSize: String(inventory.pageSize ?? 8) })
  if (inventory.search?.trim()) query.set('search', inventory.search.trim())
  return fetchJson<PaginatedResponse<WebService>>(
    `${apiBaseUrl}/api/v1/web-services?${query}`,
    {
      headers: buildHeaders(token, tenantId),
      signal,
    },
  )
}

export async function fetchWebService(
  apiBaseUrl: string,
  id: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return fetchJson<WebService>(`${apiBaseUrl}/api/v1/web-services/${encodeURIComponent(id)}`, {
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

export async function fetchDomainZones(
  apiBaseUrl: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return domainRequest<TenantDomainZone[]>(apiBaseUrl, '/domain-zones', token, tenantId, signal)
}

const domainProblems: Record<string, string> = {
  'domains.zone.not-found': 'This zone is unavailable in the active tenant. Refresh the catalog.',
  'domains.zone.already-reserved': 'This zone label is already reserved on the target. Choose another label.',
  'domains.zone.delete.generated': 'Generated zones are maintained by deployment ownership and cannot be removed here.',
  'domains.zone.delete.has-hostname-bindings': 'Detach the zone’s hostname aliases from their owning resources before removing the reservation.',
  'domains.zone.delete.conflict': 'The zone changed. Refresh its ownership before trying again.',
  'domains.zone.delete.reference-removal-rejected': 'The target could not release this reservation. Refresh and retry.',
  'domains.hostname.already-bound': 'This hostname is already reserved in the configured routing scope. Choose another name. Other owners are not disclosed.',
  'domains.hostname.zone-target-mismatch': 'Choose a zone on this deployment target.',
  'domains.hostname.zone-not-found': 'The selected zone is unavailable in this tenant. Refresh zones.',
  'domains.hostname.zone-unavailable': 'The selected zone is unavailable. Refresh zones before retrying.',
  'domains.hostname.zone-changed': 'The zone changed during the update. Refresh hostnames and retry.',
  'domains.hostname.not-found': 'This alias has already been removed or is unavailable. Refresh hostnames.',
  'domains.hostname.delete.generated': 'Generated hostnames are removed with their deployment. Detach only custom aliases here.',
  'domains.hostname.delete.conflict': 'The alias changed. Refresh hostnames before retrying.',
  'web-services.private-hostname-unavailable': 'Enable public exposure in Configuration before attaching a hostname.',
  'deployment-targets.public-ingress-unavailable': 'This target needs a configured ingress class, issuer and public base domain. Ask the platform administrator to configure public ingress.',
  'deployment-targets.workloads-unavailable': 'This target is unavailable for new workloads. Refresh targets.',
}

export class DomainApiError extends ApiError {
  readonly code: string
  constructor(status: number, code = '') {
    super(domainProblems[code] ?? (status === 403 ? 'Your tenant role cannot perform this domain action.' : status === 400 ? 'Check the zone and hostname labels. Use valid DNS labels.' : 'The domain request failed. Refresh ownership and target state before retrying.'), status)
    this.code = code
  }
}

async function domainRequest<T>(base: string, path: string, token?: string, tenantId?: string, signal?: AbortSignal, method = 'GET', body?: unknown): Promise<T> {
  let response: Response
  try { response = await fetch(`${base}/api/v1${path}`, { method, headers: { ...buildHeaders(token, tenantId), ...(body ? { 'Content-Type': 'application/json' } : {}) }, signal, cache: 'no-store', ...(body ? { body: JSON.stringify(body) } : {}) }) }
  catch { if (signal?.aborted) throw new DOMException('Request cancelled', 'AbortError'); throw new DomainApiError(0) }
  if (!response.ok) {
    let code = ''
    try { const problem: unknown = await response.json(); if (problem && typeof problem === 'object' && 'code' in problem && typeof problem.code === 'string' && Object.hasOwn(domainProblems, problem.code)) code = problem.code } catch { /* No arbitrary server metadata in conflicts. */ }
    throw new DomainApiError(response.status, code)
  }
  if (response.status === 204) return undefined as T
  try { return await response.json() as T } catch { throw new DomainApiError(response.status) }
}

export const fetchDomainZone = (base: string, id: string, token?: string, tenantId?: string, signal?: AbortSignal) => domainRequest<TenantDomainZone>(base, `/domain-zones/${encodeURIComponent(id)}`, token, tenantId, signal)
export const fetchDomainZoneBindings = (base: string, id: string, page: number, token?: string, tenantId?: string, signal?: AbortSignal) => domainRequest<PaginatedResponse<DomainZoneBindingUsage>>(base, `/domain-zones/${encodeURIComponent(id)}/bindings?pageNumber=${page}&pageSize=25`, token, tenantId, signal)

export async function createDomainZone(
  apiBaseUrl: string,
  input: { deploymentTargetId: string; label: string },
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return domainRequest<TenantDomainZone>(apiBaseUrl, '/domain-zones', token, tenantId, signal, 'POST', input)
}

export async function deleteDomainZone(
  apiBaseUrl: string,
  id: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return domainRequest<void>(apiBaseUrl, `/domain-zones/${encodeURIComponent(id)}`, token, tenantId, signal, 'DELETE')
}

export async function fetchProtectedGreeting(
  apiBaseUrl: string,
  token?: string,
  signal?: AbortSignal,
) {
  const response = await fetch(`${apiBaseUrl}/test-keycloak`, {
    headers: buildHeaders(token),
    signal,
  })

  if (!response.ok) {
    const message = await response.text()
    throw new Error(message || `Request failed with status ${response.status}`)
  }

  return response.text()
}

export async function createResourceGroup(
  apiBaseUrl: string,
  input: ResourceGroupInput,
  token?: string,
  tenantId?: string,
) {
  return sendJson<ResourceGroup>(`${apiBaseUrl}/api/v1/resource-groups`, {
    method: 'POST',
    headers: buildHeaders(token, tenantId),
    body: JSON.stringify(input),
  })
}

export async function updateResourceGroup(
  apiBaseUrl: string,
  id: string,
  input: ResourceGroupInput,
  token?: string,
  tenantId?: string,
) {
  return sendJson<ResourceGroup>(`${apiBaseUrl}/api/v1/resource-groups/${encodeURIComponent(id)}`, {
    method: 'PUT',
    headers: buildHeaders(token, tenantId),
    body: JSON.stringify(input),
  })
}

export async function createStorageProfile(
  apiBaseUrl: string,
  input: StorageProfileInput,
  token?: string,
) {
  return sendJson<StorageProfile>(
    `${apiBaseUrl}/api/v1/platform/storage/profiles`,
    {
      method: 'POST',
      headers: buildHeaders(token),
      body: JSON.stringify(input),
    },
  )
}

export async function updateStorageProfile(
  apiBaseUrl: string,
  id: string,
  input: StorageProfileInput,
  token?: string,
) {
  return sendJson<StorageProfile>(
    `${apiBaseUrl}/api/v1/platform/storage/profiles/${id}`,
    {
      method: 'PUT',
      headers: buildHeaders(token),
      body: JSON.stringify(input),
    },
  )
}

export async function deleteResourceGroup(
  apiBaseUrl: string,
  id: string,
  token?: string,
  tenantId?: string,
) {
  const response = await fetch(`${apiBaseUrl}/api/v1/resource-groups/${encodeURIComponent(id)}`, {
    method: 'DELETE',
    headers: buildHeaders(token, tenantId),
  })

  if (!response.ok) {
    throw await responseError(response)
  }
}

const buildWebServiceSpec = (input: WebServiceInput) => ({
  image: {
    server: input.imageServer,
    name: input.imageName,
    tag: input.imageTag,
  },
  config: {
    connectionEnvironmentReferences: input.connectionEnvironmentReferences ?? {},
    secretEnvironmentReferences: input.secretEnvironmentReferences ?? {},
    environmentVariables: Object.fromEntries(
      input.environmentVariables.map((item) => [item.key, item.value]),
    ),
    portMappings: [
      {
        servicePort: input.servicePort,
        targetPort: input.targetPort,
        protocol: 'TCP',
        name: input.portName,
      },
    ],
  },
  autoscaling: input.autoscaling ?? null,
  probes: {
    startup: {
      path: input.probePath,
      port: input.probePort,
      initialDelaySeconds: 10,
      periodSeconds: 10,
      timeoutSeconds: 5,
      failureThreshold: 3,
      successThreshold: 1,
    },
    liveness: {
      path: input.probePath,
      port: input.probePort,
      initialDelaySeconds: 10,
      periodSeconds: 10,
      timeoutSeconds: 5,
      failureThreshold: 3,
      successThreshold: 1,
    },
    readiness: {
      path: input.probePath,
      port: input.probePort,
      initialDelaySeconds: 10,
      periodSeconds: 10,
      timeoutSeconds: 5,
      failureThreshold: 3,
      successThreshold: 1,
    },
  },
})

const buildWebServicePlacement = (input: WebServiceInput) => ({
  isolationScopeId: input.isolationScopeId || null,
  targets: input.targets.map((target) => ({
    deploymentTargetId: target.deploymentTargetId || null,
    fixedReplicas: target.fixedReplicas,
    computeProfileId: target.computeProfileId || null,
  })),
})

export async function createWebService(
  apiBaseUrl: string,
  input: WebServiceInput,
  token?: string,
  tenantId?: string,
) {
  return sendJson<{ webServiceId: string }>(`${apiBaseUrl}/api/v1/web-services`, {
    method: 'POST',
    headers: buildHeaders(token, tenantId),
    body: JSON.stringify({
      name: input.name,
      resourceGroupId: input.resourceGroupId,
      exposure: input.exposure,
      spec: buildWebServiceSpec(input),
      placement: buildWebServicePlacement(input),
      tags: input.tags ?? [],
    }),
  })
}

export async function updateWebService(
  apiBaseUrl: string,
  id: string,
  input: WebServiceInput,
  token?: string,
  tenantId?: string,
) {
  return sendJson<{ id: string }>(`${apiBaseUrl}/api/v1/web-services/${id}`, {
    method: 'PUT',
    headers: buildHeaders(token, tenantId),
    body: JSON.stringify({
      spec: buildWebServiceSpec(input),
      exposure: input.exposure,
      tags: input.tags ?? [],
    }),
  })
}

export async function fetchWebServiceDeployments(
  apiBaseUrl: string,
  id: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return fetchJson<WebServiceDeployment[]>(
    `${apiBaseUrl}/api/v1/web-services/${encodeURIComponent(id)}/deployments`,
    { headers: buildHeaders(token, tenantId), signal },
  )
}

export async function addWebServiceDeployment(
  apiBaseUrl: string,
  id: string,
  input: WebServiceDeploymentInput,
  token?: string,
  tenantId?: string,
) {
  return sendJson<WebServiceDeployment>(
    `${apiBaseUrl}/api/v1/web-services/${encodeURIComponent(id)}/deployments`,
    {
      method: 'POST',
      headers: buildHeaders(token, tenantId),
      body: JSON.stringify(input),
    },
  )
}

export async function updateWebServiceDeployment(
  apiBaseUrl: string,
  id: string,
  deploymentId: string,
  input: WebServiceDeploymentUpdateInput,
  token?: string,
  tenantId?: string,
) {
  return sendJson<WebServiceDeployment>(
    `${apiBaseUrl}/api/v1/web-services/${encodeURIComponent(id)}/deployments/${encodeURIComponent(deploymentId)}`,
    {
      method: 'PUT',
      headers: buildHeaders(token, tenantId),
      body: JSON.stringify(input),
    },
  )
}

export async function deleteWebServiceDeployment(
  apiBaseUrl: string,
  id: string,
  deploymentId: string,
  token?: string,
  tenantId?: string,
) {
  return sendWithoutResponse(
    `${apiBaseUrl}/api/v1/web-services/${encodeURIComponent(id)}/deployments/${encodeURIComponent(deploymentId)}`,
    { method: 'DELETE', headers: buildHeaders(token, tenantId) },
  )
}

export async function retryWebServiceDeployment(
  apiBaseUrl: string,
  id: string,
  deploymentId: string,
  input: { expectedDeploymentRevision: number; expectedOperation: 'Apply' | 'Delete' },
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return sendJson<WebServiceDeployment>(
    `${apiBaseUrl}/api/v1/web-services/${encodeURIComponent(id)}/deployments/${encodeURIComponent(deploymentId)}/retry`,
    { method: 'POST', headers: buildHeaders(token, tenantId), body: JSON.stringify(input), signal },
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
  return domainRequest<HostnameBinding[]>(apiBaseUrl, `/web-services/${encodeURIComponent(id)}/deployments/${encodeURIComponent(deploymentId)}/hostnames`, token, tenantId, signal)
}

export async function addWebServiceHostnameBinding(
  apiBaseUrl: string,
  id: string,
  deploymentId: string,
  input: { tenantDomainZoneId: string; relativeName: string },
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return domainRequest<WebServiceDeployment>(apiBaseUrl, `/web-services/${encodeURIComponent(id)}/deployments/${encodeURIComponent(deploymentId)}/hostnames`, token, tenantId, signal, 'POST', input)
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
  return domainRequest<WebServiceDeployment>(apiBaseUrl, `/web-services/${encodeURIComponent(id)}/deployments/${encodeURIComponent(deploymentId)}/hostnames/${encodeURIComponent(bindingId)}`, token, tenantId, signal, 'DELETE')
}

export async function fetchWebServiceReleases(
  apiBaseUrl: string,
  id: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
  pagination: { pageNumber?: number; pageSize?: number } = {},
) {
  const query = new URLSearchParams({ pageNumber: String(pagination.pageNumber ?? 1), pageSize: String(pagination.pageSize ?? 10) })
  return releaseRequest<WebServiceReleaseHistory>(apiBaseUrl, id, `/releases?${query}`, token, tenantId, signal)
}

async function releaseRequest<T>(base: string, id: string, path: string, token?: string, tenantId?: string, signal?: AbortSignal, method = 'GET', body?: unknown): Promise<T> {
  const response = await fetch(`${base}/api/v1/web-services/${encodeURIComponent(id)}${path}`, {
    method, headers: { ...buildHeaders(token, tenantId), ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) }, signal, ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  })
  if (!response.ok) throw new Error(response.status === 409 ? 'Release state changed or recovery is no longer eligible. Refresh and review the selection again.'
    : response.status === 403 ? 'Your role cannot perform release recovery.' : response.status === 404 ? 'The service or succeeded release is unavailable in this tenant.'
    : response.status === 422 ? 'Recovery was rejected by current quota, exposure or dependency requirements. Review the current configuration.'
    : 'Release history or recovery is unavailable. Refresh to reconnect.')
  return response.json() as Promise<T>
}

export function redeployWebService(base: string, id: string, expectedRevision: number, token?: string, tenantId?: string, signal?: AbortSignal) {
  return releaseRequest<import('./types').WebServiceRecoveryResponse>(base, id, `/redeploy?${new URLSearchParams({ expectedRevision: String(expectedRevision) })}`, token, tenantId, signal, 'POST')
}

export function rollbackWebService(base: string, id: string, input: { targetRevision: number; expectedRevision: number }, token?: string, tenantId?: string, signal?: AbortSignal) {
  return releaseRequest<import('./types').WebServiceRecoveryResponse>(base, id, '/rollback', token, tenantId, signal, 'POST', input)
}

export async function deleteWebService(
  apiBaseUrl: string,
  id: string,
  token?: string,
  tenantId?: string,
) {
  const response = await fetch(`${apiBaseUrl}/api/v1/web-services/${id}`, {
    method: 'DELETE',
    headers: buildHeaders(token, tenantId),
  })

  if (!response.ok) {
    throw await responseError(response)
  }
}

export async function fetchGitProviderConnections(
  apiBaseUrl: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return fetchJson<GitProviderConnection[]>(
    `${apiBaseUrl}/api/v1/git-provider-connections`,
    {
      headers: buildHeaders(token, tenantId),
      signal,
    },
  )
}

export async function beginGitHubInstallation(
  apiBaseUrl: string,
  token?: string,
  tenantId?: string,
) {
  return sendJson<BeginGitHubInstallationResponse>(
    `${apiBaseUrl}/api/v1/git-provider-connections/github/install`,
    {
      method: 'POST',
      headers: buildHeaders(token, tenantId),
    },
  )
}

export async function disconnectGitProviderConnection(
  apiBaseUrl: string,
  connectionId: string,
  token?: string,
  tenantId?: string,
) {
  return sendWithoutResponse(
    `${apiBaseUrl}/api/v1/git-provider-connections/${encodeURIComponent(connectionId)}`,
    {
      method: 'DELETE',
      headers: buildHeaders(token, tenantId),
    },
  )
}

export async function fetchProviderRepositories(
  apiBaseUrl: string,
  connectionId: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
  page = 1,
) {
  return fetchJson<GitProviderPage<ProviderRepository>>(
    `${apiBaseUrl}/api/v1/git-provider-connections/${encodeURIComponent(connectionId)}/repositories?page=${page}&pageSize=100`,
    {
      headers: buildHeaders(token, tenantId),
      signal,
    },
  )
}

export async function fetchProviderBranches(
  apiBaseUrl: string,
  connectionId: string,
  repositoryId: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
  page = 1,
) {
  return fetchJson<GitProviderPage<ProviderBranch>>(
    `${apiBaseUrl}/api/v1/git-provider-connections/${encodeURIComponent(connectionId)}/repositories/${encodeURIComponent(repositoryId)}/branches?page=${page}&pageSize=100`,
    {
      headers: buildHeaders(token, tenantId),
      signal,
    },
  )
}

export async function analyzeProviderRepository(
  apiBaseUrl: string,
  connectionId: string,
  repositoryId: string,
  branch: string,
  token?: string,
  tenantId?: string,
) {
  return sendJson<RepositoryAnalysis>(
    `${apiBaseUrl}/api/v1/git-provider-connections/${encodeURIComponent(connectionId)}/repositories/${encodeURIComponent(repositoryId)}/analysis`,
    {
      method: 'POST',
      headers: buildHeaders(token, tenantId),
      body: JSON.stringify({ branch }),
    },
  )
}

export async function linkProviderRepository(
  apiBaseUrl: string,
  connectionId: string,
  repositoryId: string,
  branch: string,
  candidateKey: string,
  token?: string,
  tenantId?: string,
) {
  return sendJson<SourceRepository>(
    `${apiBaseUrl}/api/v1/git-provider-connections/${encodeURIComponent(connectionId)}/repositories/${encodeURIComponent(repositoryId)}/links`,
    {
      method: 'POST',
      headers: buildHeaders(token, tenantId),
      body: JSON.stringify({ branch, candidateKey }),
    },
  )
}

export async function fetchSourceRepositories(
  apiBaseUrl: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return fetchJson<SourceRepository[]>(`${apiBaseUrl}/api/v1/source-repositories`, {
    headers: buildHeaders(token, tenantId),
    signal,
  })
}

export async function reanalyzeSourceRepository(
  apiBaseUrl: string,
  sourceRepositoryId: string,
  token?: string,
  tenantId?: string,
) {
  return sendJson<SourceRepository>(
    `${apiBaseUrl}/api/v1/source-repositories/${encodeURIComponent(sourceRepositoryId)}/reanalyze`,
    {
      method: 'POST',
      headers: buildHeaders(token, tenantId),
    },
  )
}

export async function unlinkSourceRepository(
  apiBaseUrl: string,
  sourceRepositoryId: string,
  token?: string,
  tenantId?: string,
) {
  return sendWithoutResponse(
    `${apiBaseUrl}/api/v1/source-repositories/${encodeURIComponent(sourceRepositoryId)}`,
    {
      method: 'DELETE',
      headers: buildHeaders(token, tenantId),
    },
  )
}

export async function fetchSourceBuildConfigurations(
  apiBaseUrl: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return fetchJson<SourceBuildConfiguration[]>(
    `${apiBaseUrl}/api/v1/source-build-configurations`,
    {
      headers: buildHeaders(token, tenantId),
      signal,
    },
  )
}

export async function createSourceBuildConfiguration(
  apiBaseUrl: string,
  sourceRepositoryId: string,
  webServiceId: string,
  token?: string,
  tenantId?: string,
) {
  return sendJson<SourceBuildConfiguration>(
    `${apiBaseUrl}/api/v1/source-build-configurations`,
    {
      method: 'POST',
      headers: buildHeaders(token, tenantId),
      body: JSON.stringify({ sourceRepositoryId, webServiceId }),
    },
  )
}

export async function deleteSourceBuildConfiguration(
  apiBaseUrl: string,
  configurationId: string,
  token?: string,
  tenantId?: string,
) {
  return sendWithoutResponse(
    `${apiBaseUrl}/api/v1/source-build-configurations/${encodeURIComponent(configurationId)}`,
    {
      method: 'DELETE',
      headers: buildHeaders(token, tenantId),
    },
  )
}

export async function updateSourceBuildRecipe(
  apiBaseUrl: string, configurationId: string, recipe: SourceBuildRecipe, token?: string, tenantId?: string,
) {
  return sendJson<SourceBuildConfiguration>(`${apiBaseUrl}/api/v1/source-build-configurations/${encodeURIComponent(configurationId)}`, {
    method: 'PUT', headers: buildHeaders(token, tenantId), body: JSON.stringify({ recipe }),
  })
}

export async function startSourceBuild(
  apiBaseUrl: string,
  configurationId: string,
  token?: string,
  tenantId?: string,
) {
  return sendJson<SourceBuildJob>(
    `${apiBaseUrl}/api/v1/source-build-configurations/${encodeURIComponent(configurationId)}/builds`,
    {
      method: 'POST',
      headers: buildHeaders(token, tenantId),
    },
  )
}

export async function fetchSourceBuildJobs(
  apiBaseUrl: string,
  webServiceId: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  const query = new URLSearchParams({
    webServiceId,
    pageNumber: '1',
    pageSize: '20',
  })
  return fetchJson<PaginatedResponse<SourceBuildJob>>(
    `${apiBaseUrl}/api/v1/source-build-jobs?${query.toString()}`,
    {
      headers: buildHeaders(token, tenantId),
      signal,
    },
  )
}

export async function searchResources(
  apiBaseUrl: string, query: string, token?: string, tenantId?: string, signal?: AbortSignal, pageNumber = 1,
) {
  const params = new URLSearchParams({ query: query.trim(), pageNumber: String(pageNumber), pageSize: '20' })
  return fetchJson<PaginatedResponse<SearchHit>>(`${apiBaseUrl}/api/v1/search?${params}`, {
    headers: buildHeaders(token, tenantId), signal,
  })
}

export async function fetchPostgresClusters(apiBaseUrl: string, token?: string, tenantId?: string, signal?: AbortSignal, pageNumber = 1, pageSize = 8) {
  const query = new URLSearchParams({ pageNumber: String(pageNumber), pageSize: String(pageSize) })
  return fetchJson<PaginatedResponse<PostgresCluster>>(`${apiBaseUrl}/api/v1/postgres/?${query}`, { headers: buildHeaders(token, tenantId), signal })
}

export async function retryResourceDeployment(apiBaseUrl: string, deploymentId: string, expectedRevision: number, expectedOperation: string, token?: string, tenantId?: string, signal?: AbortSignal) {
  return healthRequest<unknown>(apiBaseUrl, `/${encodeURIComponent(deploymentId)}/retry?${new URLSearchParams({ expectedRevision: String(expectedRevision), expectedOperation })}`, token, tenantId, signal, 'POST')
}

async function healthRequest<T>(base: string, path: string, token?: string, tenantId?: string, signal?: AbortSignal, method = 'GET'): Promise<T> {
  const response = await fetch(`${base}/api/v1/resource-deployments${path}`, { method, headers: buildHeaders(token, tenantId), signal })
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

export async function fetchPostgresCluster(apiBaseUrl: string, id: string, token?: string, tenantId?: string, signal?: AbortSignal) {
  return fetchJson<PostgresCluster>(`${apiBaseUrl}/api/v1/postgres/${encodeURIComponent(id)}`, { headers: buildHeaders(token, tenantId), signal })
}

export async function fetchPostgresCapabilities(apiBaseUrl: string, token?: string, tenantId?: string, signal?: AbortSignal) {
  return fetchJson<PostgresCapabilities>(`${apiBaseUrl}/api/v1/postgres/supported-versions`, { headers: buildHeaders(token, tenantId), signal })
}

export async function createPostgresCluster(apiBaseUrl: string, input: PostgresInput, token?: string, tenantId?: string, signal?: AbortSignal) {
  return sendJson<PostgresCluster>(`${apiBaseUrl}/api/v1/postgres/`, {
    method: 'POST', headers: buildHeaders(token, tenantId), body: JSON.stringify(input), signal,
  })
}

export async function updatePostgresCluster(apiBaseUrl: string, id: string, input: PostgresUpdateInput, token?: string, tenantId?: string, signal?: AbortSignal) {
  return sendJson<Pick<PostgresCluster, 'id' | 'placement'>>(`${apiBaseUrl}/api/v1/postgres/${encodeURIComponent(id)}`, {
    method: 'PUT', headers: buildHeaders(token, tenantId), body: JSON.stringify(input), signal,
  })
}

export async function deletePostgresCluster(apiBaseUrl: string, id: string, token?: string, tenantId?: string, signal?: AbortSignal) {
  return sendWithoutResponse(`${apiBaseUrl}/api/v1/postgres/${encodeURIComponent(id)}`, { method: 'DELETE', headers: buildHeaders(token, tenantId), signal })
}

export async function fetchPostgresConnectionString(apiBaseUrl: string, id: string, token?: string, tenantId?: string, signal?: AbortSignal) {
  return fetchJson<{ connectionString: string }>(`${apiBaseUrl}/api/v1/postgres/${encodeURIComponent(id)}/connection-string`, {
    headers: buildHeaders(token, tenantId), signal, cache: 'no-store',
  })
}

export async function rotatePostgresPassword(apiBaseUrl: string, id: string, password: string, token?: string, tenantId?: string, signal?: AbortSignal) {
  return sendJson<Pick<PostgresCluster, 'id' | 'placement'>>(`${apiBaseUrl}/api/v1/postgres/${encodeURIComponent(id)}/password`, {
    method: 'PATCH', headers: buildHeaders(token, tenantId), body: JSON.stringify({ password }), signal,
  })
}

export async function fetchRedisServices(apiBaseUrl: string, token?: string, tenantId?: string, signal?: AbortSignal, pageNumber = 1, pageSize = 8, search = '') {
  const query = new URLSearchParams({ pageNumber: String(pageNumber), pageSize: String(pageSize) })
  if (search.trim()) query.set('search', search.trim())
  return fetchJson<PaginatedResponse<RedisService>>(`${apiBaseUrl}/api/v1/redis-services/?${query}`, { headers: buildHeaders(token, tenantId), signal })
}

export async function fetchRedisService(apiBaseUrl: string, id: string, token?: string, tenantId?: string, signal?: AbortSignal) {
  return fetchJson<RedisService>(`${apiBaseUrl}/api/v1/redis-services/${encodeURIComponent(id)}`, { headers: buildHeaders(token, tenantId), signal })
}

export async function createRedisService(apiBaseUrl: string, input: RedisInput, token?: string, tenantId?: string, signal?: AbortSignal) {
  return sendJson<{ redisId: string; placement: ResourcePlacement }>(`${apiBaseUrl}/api/v1/redis-services/`, {
    method: 'POST', headers: buildHeaders(token, tenantId), body: JSON.stringify(input), signal,
  })
}

export async function updateRedisService(apiBaseUrl: string, id: string, input: RedisUpdateInput, token?: string, tenantId?: string, signal?: AbortSignal) {
  return sendJson<Pick<RedisService, 'id' | 'placement'>>(`${apiBaseUrl}/api/v1/redis-services/${encodeURIComponent(id)}`, {
    method: 'PUT', headers: buildHeaders(token, tenantId), body: JSON.stringify(input), signal,
  })
}

export async function deleteRedisService(apiBaseUrl: string, id: string, token?: string, tenantId?: string, signal?: AbortSignal) {
  return sendWithoutResponse(`${apiBaseUrl}/api/v1/redis-services/${encodeURIComponent(id)}`, { method: 'DELETE', headers: buildHeaders(token, tenantId), signal })
}

async function sendWithoutResponse(input: RequestInfo | URL, init: RequestInit) {
  const response = await fetch(input, init)
  if (!response.ok) {
    throw await responseError(response)
  }
}
