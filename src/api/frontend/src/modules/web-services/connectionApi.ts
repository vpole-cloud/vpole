// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { buildHeaders, fetchJson, sendJson } from '../../api/transport'
import type { PaginatedResponse } from '../../types'
import type { ResourceConnection } from './connectionTypes'

export function fetchResourceConnections(
  base: string,
  sourceResourceId: string,
  pageNumber: number,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  const query = new URLSearchParams({
    sourceResourceType: 'pole/web-service',
    sourceResourceId,
    pageNumber: String(pageNumber),
    pageSize: '100',
  })
  return fetchJson<PaginatedResponse<ResourceConnection>>(
    `${base}/api/v1/resource-connections/?${query}`,
    { headers: buildHeaders(token, tenantId), signal },
  )
}

export function createResourceConnection(
  base: string,
  input: {
    name: string
    sourceResourceType: string
    sourceResourceId: string
    destinationResourceType: string
    destinationResourceId: string
    endpointId: string
  },
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return sendJson<{ id: string; name: string; generation: number; status: string }>(
    `${base}/api/v1/resource-connections/`,
    { method: 'POST', headers: buildHeaders(token, tenantId), body: JSON.stringify(input), signal },
  )
}

export function deleteResourceConnection(
  base: string,
  id: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return sendJson<{ id: string; generation: number; status: string }>(
    `${base}/api/v1/resource-connections/${encodeURIComponent(id)}`,
    { method: 'DELETE', headers: buildHeaders(token, tenantId), signal },
  )
}
