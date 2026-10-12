import { buildHeaders, fetchJson, request, responseError } from '../../api/transport'
import type { PaginatedResponse } from '../../types'
import type { SearchHit } from './overviewTypes'

export async function fetchProtectedGreeting(
  apiBaseUrl: string,
  token?: string,
  signal?: AbortSignal,
) {
  const response = await request(`${apiBaseUrl}/test-keycloak`, {
    headers: buildHeaders(token),
    signal,
  })

  if (!response.ok) throw await responseError(response)

  return response.text()
}

export async function searchResources(
  apiBaseUrl: string, query: string, token?: string, tenantId?: string, signal?: AbortSignal, pageNumber = 1,
) {
  const params = new URLSearchParams({ query: query.trim(), pageNumber: String(pageNumber), pageSize: '20' })
  return fetchJson<PaginatedResponse<SearchHit>>(`${apiBaseUrl}/api/v1/search?${params}`, {
    headers: buildHeaders(token, tenantId), signal,
  })
}
