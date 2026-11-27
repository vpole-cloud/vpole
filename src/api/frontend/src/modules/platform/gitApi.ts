// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { buildHeaders, fetchJson, sendJson, sendWithoutResponse } from '../../api/transport'
import type {
  BeginGitHubInstallationResponse,
  GitProviderConnection,
  GitProviderPage,
  ProviderBranch,
  ProviderRepository,
  RepositoryAnalysis,
  SourceRepository,
} from './gitTypes'

export async function fetchGitProviderConnections(
  apiBaseUrl: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return fetchJson<GitProviderConnection[]>(`${apiBaseUrl}/api/v1/git-provider-connections`, {
    headers: buildHeaders(token, tenantId),
    signal,
  })
}

export async function beginGitHubInstallation(
  apiBaseUrl: string,
  technicalName: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return sendJson<BeginGitHubInstallationResponse>(
    `${apiBaseUrl}/api/v1/git-provider-connections/github/install`,
    {
      method: 'POST',
      body: JSON.stringify({ technicalName }),
      headers: buildHeaders(token, tenantId),
      signal,
    },
  )
}

export async function disconnectGitProviderConnection(
  apiBaseUrl: string,
  connectionId: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return sendWithoutResponse(
    `${apiBaseUrl}/api/v1/git-provider-connections/${encodeURIComponent(connectionId)}`,
    {
      method: 'DELETE',
      headers: buildHeaders(token, tenantId),
      signal,
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
  signal?: AbortSignal,
) {
  return sendJson<RepositoryAnalysis>(
    `${apiBaseUrl}/api/v1/git-provider-connections/${encodeURIComponent(connectionId)}/repositories/${encodeURIComponent(repositoryId)}/analysis`,
    {
      method: 'POST',
      headers: buildHeaders(token, tenantId),
      signal,
      body: JSON.stringify({ branch }),
    },
  )
}

export async function linkProviderRepository(
  apiBaseUrl: string,
  connectionId: string,
  repositoryId: string,
  branch: string,
  technicalName: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return sendJson<SourceRepository>(
    `${apiBaseUrl}/api/v1/git-provider-connections/${encodeURIComponent(connectionId)}/repositories/${encodeURIComponent(repositoryId)}/links`,
    {
      method: 'POST',
      headers: buildHeaders(token, tenantId),
      signal,
      body: JSON.stringify({ branch, technicalName }),
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
  signal?: AbortSignal,
) {
  return sendJson<RepositoryAnalysis>(
    `${apiBaseUrl}/api/v1/source-repositories/${encodeURIComponent(sourceRepositoryId)}/reanalyze`,
    {
      method: 'POST',
      headers: buildHeaders(token, tenantId),
      signal,
    },
  )
}

export async function unlinkSourceRepository(
  apiBaseUrl: string,
  sourceRepositoryId: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return sendWithoutResponse(
    `${apiBaseUrl}/api/v1/source-repositories/${encodeURIComponent(sourceRepositoryId)}`,
    {
      method: 'DELETE',
      headers: buildHeaders(token, tenantId),
      signal,
    },
  )
}
