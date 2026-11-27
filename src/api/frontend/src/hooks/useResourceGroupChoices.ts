// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { useApiScope } from '../data/useApiScope'
import { fetchResourceGroups } from '../api'
import { useScopedQuery } from '../data/useScopedQuery'
import { groupInterests } from '../realtime/resourceInterests'

export function useResourceGroupChoices(api: {
  apiBaseUrl: string
  token?: string
  tenantId?: string
  enabled: boolean
  active?: boolean
}) {
  const { credential } = useApiScope({
    baseUrl: api.apiBaseUrl,
    tenantId: api.tenantId,
    token: api.token,
  })
  const query = useScopedQuery({
    scope: [api.apiBaseUrl, api.tenantId],
    key: ['resource-groups', 'choices'],
    enabled: api.enabled,
    active: api.active,
    realtime: groupInterests(api.tenantId),
    load: async (signal) => {
      const first = await fetchResourceGroups(api.apiBaseUrl, credential(), api.tenantId, signal, {
        pageSize: 200,
      })
      const items = [...first.items]
      for (let pageNumber = 2; items.length < first.totalCount; pageNumber++) {
        if (signal.aborted) throw new DOMException('Aborted', 'AbortError')
        const next = await fetchResourceGroups(api.apiBaseUrl, credential(), api.tenantId, signal, {
          pageNumber,
          pageSize: 200,
        })
        items.push(...next.items)
        if (!next.items.length) break
      }
      return [...new Map(items.map((item) => [item.id, item])).values()]
    },
  })
  return { ...query, items: query.resource ?? [] }
}
