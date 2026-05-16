import type {
  PaginatedResponse,
  ResourceGroup,
  ResourceGroupInput,
  WebServiceInput,
  WebService,
  Tenant,
} from './types'

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
  return fetchJson<Tenant[]>(`${apiBaseUrl}/api/v1/tenants`, {
    headers: buildHeaders(token),
    signal,
  })
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
  replicas: input.replicas,
  resources: {
    requests: {
      cpuCores: input.requestCpuCores,
      memoryMi: input.requestMemoryMi,
    },
    limits: {
      cpuCores: input.limitCpuCores,
      memoryMi: input.limitMemoryMi,
    },
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
  autoscaling: null,
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
      domainName: input.domainName,
      spec: buildWebServiceSpec(input),
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
      resourceGroupId: input.resourceGroupId,
      spec: buildWebServiceSpec(input),
    }),
  })
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
