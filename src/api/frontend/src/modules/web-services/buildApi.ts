// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import {
  ApiError,
  buildHeaders,
  fetchJson,
  request,
  sendJson,
  sendWithoutResponse,
} from '../../api/transport'
import type { PaginatedResponse } from '../../types'
import type {
  SourceBuildConfiguration,
  SourceBuildRecipe,
  SourceBuildJob,
  SourceBuildLogs,
} from './buildTypes'

async function buildLogRequest<T>(
  base: string,
  jobId: string,
  suffix: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
): Promise<T> {
  const response = await request(
    `${base}/api/v1/source-build-jobs/${encodeURIComponent(jobId)}${suffix}`,
    { headers: buildHeaders(token, tenantId), signal },
  )
  if (!response.ok)
    throw new ApiError(
      response.status === 400
        ? 'The log cursor is unavailable. Restart this log view to request its retained beginning.'
        : response.status === 403
          ? 'Your current tenant role cannot read this build.'
          : response.status === 404
            ? 'This build is unavailable for the selected service and tenant.'
            : 'Build logs are unavailable. Reconnect to resume from the last displayed sequence.',
      response.status,
    )
  return response.json() as Promise<T>
}

export function fetchSourceBuildJob(
  base: string,
  jobId: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return buildLogRequest<SourceBuildJob>(base, jobId, '', token, tenantId, signal)
}

export function fetchSourceBuildLogs(
  base: string,
  jobId: string,
  webServiceId: string,
  afterSequence: number,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return buildLogRequest<SourceBuildLogs>(
    base,
    jobId,
    `/logs?${new URLSearchParams({ webServiceId, afterSequence: String(afterSequence) })}`,
    token,
    tenantId,
    signal,
  )
}

export async function fetchSourceBuildConfigurations(
  apiBaseUrl: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return fetchJson<SourceBuildConfiguration[]>(`${apiBaseUrl}/api/v1/source-build-configurations`, {
    headers: buildHeaders(token, tenantId),
    signal,
  })
}

export async function createSourceBuildConfiguration(
  apiBaseUrl: string,
  body: {
    technicalName: string
    sourceRepositoryId: string
    webServiceId: string
    recipe: SourceBuildRecipe
  },
  token?: string,
  tenantId?: string,
) {
  return sendJson<SourceBuildConfiguration>(`${apiBaseUrl}/api/v1/source-build-configurations`, {
    method: 'POST',
    headers: buildHeaders(token, tenantId),
    body: JSON.stringify(body),
  })
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
  apiBaseUrl: string,
  configurationId: string,
  body: { recipe: SourceBuildRecipe; expectedRevision: number },
  token?: string,
  tenantId?: string,
) {
  return sendJson<SourceBuildConfiguration>(
    `${apiBaseUrl}/api/v1/source-build-configurations/${encodeURIComponent(configurationId)}`,
    {
      method: 'PUT',
      headers: buildHeaders(token, tenantId),
      body: JSON.stringify(body),
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
