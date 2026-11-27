// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { buildHeaders, fetchJson, sendJson, sendWithoutResponse } from '../../api/transport'
import type { PaginatedResponse, ResourceRegistration } from '../../types'
import type { IsolationScope, ResourceGroup, ResourceGroupInput } from './types'

export async function fetchIsolationScopes(
  apiBaseUrl: string,
  resourceGroupId: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return fetchJson<IsolationScope[]>(
    `${apiBaseUrl}/api/v1/resource-groups/${encodeURIComponent(resourceGroupId)}/isolation-scopes`,
    {
      headers: buildHeaders(token, tenantId),
      signal,
    },
  )
}

export async function fetchResourceGroups(
  apiBaseUrl: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
  inventory: { pageNumber?: number; pageSize?: number; search?: string } = {},
) {
  const query = new URLSearchParams({
    pageNumber: String(inventory.pageNumber ?? 1),
    pageSize: String(inventory.pageSize ?? 6),
  })
  if (inventory.search?.trim()) query.set('search', inventory.search.trim())
  return fetchJson<PaginatedResponse<ResourceGroup>>(
    `${apiBaseUrl}/api/v1/resource-groups?${query}`,
    {
      headers: buildHeaders(token, tenantId),
      signal,
    },
  )
}

export async function fetchResourceGroup(
  apiBaseUrl: string,
  id: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return fetchJson<ResourceGroup>(
    `${apiBaseUrl}/api/v1/resource-groups/${encodeURIComponent(id)}`,
    {
      headers: buildHeaders(token, tenantId),
      signal,
    },
  )
}

export async function fetchResourceGroupResources(
  apiBaseUrl: string,
  resourceGroupId: string,
  pageNumber = 1,
  pageSize = 20,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  const query = new URLSearchParams({
    pageNumber: pageNumber.toString(),
    pageSize: pageSize.toString(),
  })

  return fetchJson<PaginatedResponse<ResourceRegistration>>(
    `${apiBaseUrl}/api/v1/resource-groups/${encodeURIComponent(resourceGroupId)}/resources?${query.toString()}`,
    {
      headers: buildHeaders(token, tenantId),
      signal,
    },
  )
}

export async function createResourceGroup(
  apiBaseUrl: string,
  input: ResourceGroupInput,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return sendJson<ResourceGroup>(`${apiBaseUrl}/api/v1/resource-groups`, {
    method: 'POST',
    headers: buildHeaders(token, tenantId),
    signal,
    body: JSON.stringify(input),
  })
}

export async function updateResourceGroup(
  apiBaseUrl: string,
  id: string,
  input: ResourceGroupInput,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return sendJson<ResourceGroup>(`${apiBaseUrl}/api/v1/resource-groups/${encodeURIComponent(id)}`, {
    method: 'PUT',
    headers: buildHeaders(token, tenantId),
    signal,
    body: JSON.stringify(input),
  })
}

export async function deleteResourceGroup(
  apiBaseUrl: string,
  id: string,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  return sendWithoutResponse(`${apiBaseUrl}/api/v1/resource-groups/${encodeURIComponent(id)}`, {
    method: 'DELETE',
    headers: buildHeaders(token, tenantId),
    signal,
  })
}
