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
  WebServiceInput,
  WebService,
  Tenant,
  TenantRole,
  TenantDomainZone,
  HostnameBinding,
  WebServiceDeployment,
  WebServiceDeploymentInput,
  WebServiceDeploymentUpdateInput,
  WebServiceReleaseHistory,
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
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  const query = new URLSearchParams({ resourceType })

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

async function fetchJson<T>(
  input: RequestInfo | URL,
  init?: RequestInit,
): Promise<T> {
  const response = await fetch(input, init)

  if (!response.ok) {
    const message = await response.text()
    throw new Error(message || `Request failed with status ${response.status}`)
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
) {
  return fetchJson<PaginatedResponse<ResourceGroup>>(
    `${apiBaseUrl}/api/v1/resource-groups?pageNumber=1&pageSize=6`,
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
) {
  return fetchJson<PaginatedResponse<WebService>>(
    `${apiBaseUrl}/api/v1/web-services?pageNumber=1&pageSize=8`,
    {
      headers: buildHeaders(token, tenantId),
      signal,
    },
  )
}

const webServiceResourceType = 'pole/web-service'

export async function fetchAvailableDeploymentTargets(
  apiBaseUrl: string,
  isolationScopeId: string | undefined,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  const query = new URLSearchParams({
    resourceType: webServiceResourceType,
    requiresPublicIngress: 'true',
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
) {
  const query = new URLSearchParams({
    deploymentTargetId,
    resourceType: webServiceResourceType,
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
  return fetchJson<TenantDomainZone[]>(`${apiBaseUrl}/api/v1/domain-zones`, {
    headers: buildHeaders(token, tenantId),
    signal,
  })
}

export async function createDomainZone(
  apiBaseUrl: string,
  input: { deploymentTargetId: string; label: string },
  token?: string,
  tenantId?: string,
) {
  return sendJson<TenantDomainZone>(`${apiBaseUrl}/api/v1/domain-zones`, {
    method: 'POST',
    headers: buildHeaders(token, tenantId),
    body: JSON.stringify(input),
  })
}

export async function deleteDomainZone(
  apiBaseUrl: string,
  id: string,
  token?: string,
  tenantId?: string,
) {
  return sendWithoutResponse(`${apiBaseUrl}/api/v1/domain-zones/${encodeURIComponent(id)}`, {
    method: 'DELETE',
    headers: buildHeaders(token, tenantId),
  })
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
  return sendJson<ResourceGroup>(`${apiBaseUrl}/api/v1/resource-groups/${id}`, {
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
  const response = await fetch(`${apiBaseUrl}/api/v1/resource-groups/${id}`, {
    method: 'DELETE',
    headers: buildHeaders(token, tenantId),
  })

  if (!response.ok) {
    const message = await response.text()
    throw new Error(message || `Request failed with status ${response.status}`)
  }
}

const buildWebServiceSpec = (input: WebServiceInput) => ({
  image: {
    server: input.imageServer,
    name: input.imageName,
    tag: input.imageTag,
  },
  config: {
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
      spec: buildWebServiceSpec(input),
      placement: buildWebServicePlacement(input),
      tags: [],
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
      tags: [],
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
) {
  return sendJson<WebServiceDeployment>(
    `${apiBaseUrl}/api/v1/web-services/${encodeURIComponent(id)}/deployments/${encodeURIComponent(deploymentId)}/retry`,
    { method: 'POST', headers: buildHeaders(token, tenantId), body: JSON.stringify(input) },
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
  return fetchJson<HostnameBinding[]>(
    `${apiBaseUrl}/api/v1/web-services/${encodeURIComponent(id)}/deployments/${encodeURIComponent(deploymentId)}/hostnames`,
    { headers: buildHeaders(token, tenantId), signal },
  )
}

export async function addWebServiceHostnameBinding(
  apiBaseUrl: string,
  id: string,
  deploymentId: string,
  input: { tenantDomainZoneId: string; relativeName: string },
  token?: string,
  tenantId?: string,
) {
  return sendJson<WebServiceDeployment>(
    `${apiBaseUrl}/api/v1/web-services/${encodeURIComponent(id)}/deployments/${encodeURIComponent(deploymentId)}/hostnames`,
    { method: 'POST', headers: buildHeaders(token, tenantId), body: JSON.stringify(input) },
  )
}

export async function deleteWebServiceHostnameBinding(
  apiBaseUrl: string,
  id: string,
  deploymentId: string,
  bindingId: string,
  token?: string,
  tenantId?: string,
) {
  return sendJson<WebServiceDeployment>(
    `${apiBaseUrl}/api/v1/web-services/${encodeURIComponent(id)}/deployments/${encodeURIComponent(deploymentId)}/hostnames/${encodeURIComponent(bindingId)}`,
    { method: 'DELETE', headers: buildHeaders(token, tenantId) },
  )
}

export async function fetchWebServiceReleases(
  apiBaseUrl: string,
  id: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return fetchJson<WebServiceReleaseHistory>(
    `${apiBaseUrl}/api/v1/web-services/${encodeURIComponent(id)}/releases?pageNumber=1&pageSize=10`,
    { headers: buildHeaders(token, tenantId), signal },
  )
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
    const message = await response.text()
    throw new Error(message || `Request failed with status ${response.status}`)
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
) {
  return fetchJson<GitProviderPage<ProviderRepository>>(
    `${apiBaseUrl}/api/v1/git-provider-connections/${encodeURIComponent(connectionId)}/repositories?page=1&pageSize=100`,
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
) {
  return fetchJson<GitProviderPage<ProviderBranch>>(
    `${apiBaseUrl}/api/v1/git-provider-connections/${encodeURIComponent(connectionId)}/repositories/${encodeURIComponent(repositoryId)}/branches?page=1&pageSize=100`,
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

async function sendWithoutResponse(input: RequestInfo | URL, init: RequestInit) {
  const response = await fetch(input, init)
  if (!response.ok) {
    const message = await response.text()
    throw new Error(message || `Request failed with status ${response.status}`)
  }
}
