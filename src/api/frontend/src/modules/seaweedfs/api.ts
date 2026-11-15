import {
  buildHeaders,
  fetchJson,
  request,
  sendJson,
  sendWithoutResponse,
} from '../../api/transport'
import type { PaginatedResponse, ResourcePlacement } from '../../types'
import type { SeaweedFSBucket, SeaweedFSInput, SeaweedFSInstance, SeaweedFSMode } from './types'

export interface SeaweedFSApi {
  baseUrl: string
  token?: string
  tenantId?: string
}

export function listSeaweedFS(
  api: SeaweedFSApi,
  pageNumber: number,
  pageSize: number,
  search: string,
  signal: AbortSignal,
) {
  const query = new URLSearchParams({
    pageNumber: String(pageNumber),
    pageSize: String(pageSize),
    search,
  })
  return fetchJson<PaginatedResponse<SeaweedFSInstance>>(
    `${api.baseUrl}/api/v1/seaweedfs/?${query}`,
    {
      headers: buildHeaders(api.token, api.tenantId),
      signal,
    },
  )
}

export function getSeaweedFS(api: SeaweedFSApi, id: string, signal: AbortSignal) {
  return fetchJson<SeaweedFSInstance>(`${api.baseUrl}/api/v1/seaweedfs/${encodeURIComponent(id)}`, {
    headers: buildHeaders(api.token, api.tenantId),
    signal,
  })
}

export function createSeaweedFS(api: SeaweedFSApi, input: SeaweedFSInput, signal: AbortSignal) {
  return sendJson<{ seaweedFSId: string; placement: ResourcePlacement }>(
    `${api.baseUrl}/api/v1/seaweedfs/`,
    {
      method: 'POST',
      headers: buildHeaders(api.token, api.tenantId),
      body: JSON.stringify(input),
      signal,
    },
  )
}

function instancePath(api: SeaweedFSApi, id: string) {
  return `${api.baseUrl}/api/v1/seaweedfs/${encodeURIComponent(id)}`
}

export function getSeaweedFSCapacity(
  api: SeaweedFSApi,
  mode: SeaweedFSMode,
  sizeGb: number,
  usesExistingPostgres: boolean,
  signal: AbortSignal,
) {
  const query = new URLSearchParams({
    mode,
    sizeGb: String(sizeGb),
    usesExistingPostgres: String(usesExistingPostgres),
  })
  return fetchJson<{ storageGb: number; workloadInstances: number }>(
    `${api.baseUrl}/api/v1/seaweedfs/capacity?${query}`,
    {
      headers: buildHeaders(api.token, api.tenantId),
      signal,
    },
  )
}

export function listSeaweedFSBuckets(api: SeaweedFSApi, id: string, signal: AbortSignal) {
  return fetchJson<SeaweedFSBucket[]>(`${instancePath(api, id)}/buckets`, {
    headers: buildHeaders(api.token, api.tenantId),
    signal,
  })
}

export function createSeaweedFSBucket(
  api: SeaweedFSApi,
  id: string,
  name: string,
  signal: AbortSignal,
) {
  return sendWithoutResponse(`${instancePath(api, id)}/buckets`, {
    method: 'POST',
    headers: { ...buildHeaders(api.token, api.tenantId), 'Content-Type': 'application/json' },
    body: JSON.stringify({ name }),
    signal,
  })
}

export function updateSeaweedFSLifecycle(
  api: SeaweedFSApi,
  id: string,
  bucketId: string | null,
  operation: 'delete' | 'restore',
  expectedRevision: number,
  signal: AbortSignal,
) {
  const base = instancePath(api, id) + (bucketId ? `/buckets/${encodeURIComponent(bucketId)}` : '')
  const headers = buildHeaders(api.token, api.tenantId)
  if (operation === 'delete') {
    return sendWithoutResponse(`${base}?expectedRevision=${expectedRevision}`, {
      method: 'DELETE',
      headers,
      signal,
    })
  }
  return sendWithoutResponse(`${base}/restore`, {
    method: 'POST',
    headers: { ...headers, 'Content-Type': 'application/json' },
    body: JSON.stringify({ expectedRevision }),
    signal,
  })
}

export function rotateSeaweedFSBucketKey(
  api: SeaweedFSApi,
  id: string,
  bucketId: string,
  expectedRevision: number,
  signal: AbortSignal,
) {
  return sendWithoutResponse(
    `${instancePath(api, id)}/buckets/${encodeURIComponent(bucketId)}/rotate-credentials`,
    {
      method: 'POST',
      headers: { ...buildHeaders(api.token, api.tenantId), 'Content-Type': 'application/json' },
      body: JSON.stringify({ expectedRevision }),
      signal,
    },
  )
}

export async function revealSeaweedFSCredential(
  api: SeaweedFSApi,
  id: string,
  bucketId: string | null,
  signal: AbortSignal,
) {
  const base = instancePath(api, id) + (bucketId ? `/buckets/${encodeURIComponent(bucketId)}` : '')
  const result = await request(`${base}/credentials`, {
    headers: buildHeaders(api.token, api.tenantId),
    cache: 'no-store',
    signal,
  })
  if (!result.ok) throw new Error('The credential could not be retrieved.')
  return (await result.json()) as { accessKey: string; secretKey: string }
}

export function updateSeaweedFSTags(
  api: SeaweedFSApi,
  id: string,
  expectedRevision: number,
  tags: string[],
  signal: AbortSignal,
) {
  return sendWithoutResponse(instancePath(api, id), {
    method: 'PUT',
    headers: { ...buildHeaders(api.token, api.tenantId), 'Content-Type': 'application/json' },
    body: JSON.stringify({ expectedRevision, tags }),
    signal,
  })
}
