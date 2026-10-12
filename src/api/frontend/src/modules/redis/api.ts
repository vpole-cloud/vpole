import { buildHeaders, fetchJson, sendJson, sendWithoutResponse } from '../../api/transport'
import type { PaginatedResponse, ResourcePlacement } from '../../types'
import type { RedisService, RedisInput, RedisUpdateInput } from './types'

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
