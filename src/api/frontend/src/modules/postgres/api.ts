import { buildHeaders, fetchJson, sendJson, sendWithoutResponse } from '../../api/transport'
import type { PaginatedResponse } from '../../types'
import type {
  PostgresCluster,
  PostgresCapabilities,
  PostgresInput,
  PostgresUpdateInput,
} from './types'

export async function fetchPostgresClusters(
  apiBaseUrl: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
  pageNumber = 1,
  pageSize = 8,
) {
  const query = new URLSearchParams({ pageNumber: String(pageNumber), pageSize: String(pageSize) })
  return fetchJson<PaginatedResponse<PostgresCluster>>(`${apiBaseUrl}/api/v1/postgres/?${query}`, {
    headers: buildHeaders(token, tenantId),
    signal,
  })
}

export async function fetchPostgresCluster(
  apiBaseUrl: string,
  id: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return fetchJson<PostgresCluster>(`${apiBaseUrl}/api/v1/postgres/${encodeURIComponent(id)}`, {
    headers: buildHeaders(token, tenantId),
    signal,
  })
}

export async function fetchPostgresCapabilities(
  apiBaseUrl: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return fetchJson<PostgresCapabilities>(`${apiBaseUrl}/api/v1/postgres/supported-versions`, {
    headers: buildHeaders(token, tenantId),
    signal,
  })
}

export async function createPostgresCluster(
  apiBaseUrl: string,
  input: PostgresInput,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return sendJson<PostgresCluster>(`${apiBaseUrl}/api/v1/postgres/`, {
    method: 'POST',
    headers: buildHeaders(token, tenantId),
    body: JSON.stringify(input),
    signal,
  })
}

export async function updatePostgresCluster(
  apiBaseUrl: string,
  id: string,
  input: PostgresUpdateInput,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return sendJson<Pick<PostgresCluster, 'id' | 'placement'>>(
    `${apiBaseUrl}/api/v1/postgres/${encodeURIComponent(id)}`,
    {
      method: 'PUT',
      headers: buildHeaders(token, tenantId),
      body: JSON.stringify(input),
      signal,
    },
  )
}

export async function deletePostgresCluster(
  apiBaseUrl: string,
  id: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return sendWithoutResponse(`${apiBaseUrl}/api/v1/postgres/${encodeURIComponent(id)}`, {
    method: 'DELETE',
    headers: buildHeaders(token, tenantId),
    signal,
  })
}

export async function fetchPostgresConnectionString(
  apiBaseUrl: string,
  id: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return fetchJson<{ connectionString: string }>(
    `${apiBaseUrl}/api/v1/postgres/${encodeURIComponent(id)}/connection-string`,
    {
      headers: buildHeaders(token, tenantId),
      signal,
      cache: 'no-store',
    },
  )
}

export async function rotatePostgresPassword(
  apiBaseUrl: string,
  id: string,
  password: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return sendJson<Pick<PostgresCluster, 'id' | 'placement'>>(
    `${apiBaseUrl}/api/v1/postgres/${encodeURIComponent(id)}/password`,
    {
      method: 'PATCH',
      headers: buildHeaders(token, tenantId),
      body: JSON.stringify({ password }),
      signal,
    },
  )
}
