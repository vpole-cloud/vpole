// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import {
  ApiError,
  buildHeaders,
  fetchJson,
  request,
  responseError,
  sendJson,
  sendWithoutResponse,
} from '../../api/transport'
import type { PaginatedResponse } from '../../types'
import type {
  WebService,
  WebServiceInput,
  WebServiceSpec,
  WebServiceDeployment,
  WebServiceDeploymentInput,
  WebServiceDeploymentUpdateInput,
  WebServiceReleaseHistory,
  WebServiceRecoveryResponse,
} from './types'

export function updateWebServiceDependencies(
  base: string,
  service: WebService,
  config: WebServiceSpec['config'],
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return sendJson<{ id: string; resourceRevision: number }>(
    `${base}/api/v1/web-services/${encodeURIComponent(service.id)}`,
    {
      method: 'PUT',
      headers: buildHeaders(token, tenantId),
      signal,
      body: JSON.stringify({
        spec: { ...service.spec, config },
        exposure: service.exposure.mode,
        tags: service.tags ?? [],
        expectedRevision: service.activeRevision,
      }),
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
  const query = new URLSearchParams({
    pageNumber: String(inventory.pageNumber ?? 1),
    pageSize: String(inventory.pageSize ?? 8),
  })
  if (inventory.search?.trim()) query.set('search', inventory.search.trim())
  return fetchJson<PaginatedResponse<WebService>>(`${apiBaseUrl}/api/v1/web-services?${query}`, {
    headers: buildHeaders(token, tenantId),
    signal,
  })
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

const buildWebServiceSpec = (input: WebServiceInput) => ({
  image:
    input.creationMode === 'SourceBuild'
      ? null
      : {
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
      creationMode: input.creationMode ?? 'Image',
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
  return sendJson<{ id: string; resourceRevision: number }>(
    `${apiBaseUrl}/api/v1/web-services/${id}`,
    {
      method: 'PUT',
      headers: buildHeaders(token, tenantId),
      body: JSON.stringify({
        expectedRevision: input.expectedRevision,
        spec: buildWebServiceSpec(input),
        exposure: input.exposure,
        tags: input.tags ?? [],
      }),
    },
  )
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

export async function fetchWebServiceReleases(
  apiBaseUrl: string,
  id: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
  pagination: { pageNumber?: number; pageSize?: number } = {},
) {
  const query = new URLSearchParams({
    pageNumber: String(pagination.pageNumber ?? 1),
    pageSize: String(pagination.pageSize ?? 10),
  })
  return releaseRequest<WebServiceReleaseHistory>(
    apiBaseUrl,
    id,
    `/releases?${query}`,
    token,
    tenantId,
    signal,
  )
}

async function releaseRequest<T>(
  base: string,
  id: string,
  path: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
  method = 'GET',
  body?: unknown,
): Promise<T> {
  const response = await request(`${base}/api/v1/web-services/${encodeURIComponent(id)}${path}`, {
    method,
    headers: {
      ...buildHeaders(token, tenantId),
      ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
    },
    signal,
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  })
  if (!response.ok)
    throw new ApiError(
      response.status === 409
        ? 'Release state changed or recovery is no longer eligible. Refresh and review the selection again.'
        : response.status === 403
          ? 'Your role cannot perform release recovery.'
          : response.status === 404
            ? 'The service or succeeded release is unavailable in this tenant.'
            : response.status === 422
              ? 'Recovery was rejected by current quota, exposure or dependency requirements. Review the current configuration.'
              : 'Release history or recovery is unavailable. Refresh to reconnect.',
      response.status,
    )
  return response.json() as Promise<T>
}

export function redeployWebService(
  base: string,
  id: string,
  expectedRevision: number,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return releaseRequest<WebServiceRecoveryResponse>(
    base,
    id,
    `/redeploy?${new URLSearchParams({ expectedRevision: String(expectedRevision) })}`,
    token,
    tenantId,
    signal,
    'POST',
  )
}

export function rollbackWebService(
  base: string,
  id: string,
  input: { targetRevision: number; expectedRevision: number },
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return releaseRequest<WebServiceRecoveryResponse>(
    base,
    id,
    '/rollback',
    token,
    tenantId,
    signal,
    'POST',
    input,
  )
}

export async function deleteWebService(
  apiBaseUrl: string,
  id: string,
  token?: string,
  tenantId?: string,
) {
  const response = await request(`${apiBaseUrl}/api/v1/web-services/${id}`, {
    method: 'DELETE',
    headers: buildHeaders(token, tenantId),
  })

  if (!response.ok) {
    throw await responseError(response)
  }
}
