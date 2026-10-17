import { fetchWebServices } from '../../api'
import { useScopedQuery } from '../../data/useScopedQuery'
import { useQuerySession } from '../../data/querySession'
import { useApiScope } from '../../data/useApiScope'

export interface WebServiceInventoryOptions {
  apiBaseUrl: string
  token?: string
  tenantId?: string
  enabled: boolean
  pageNumber?: number
  pageSize?: number
  search?: string
}

export function useWebServiceInventory({
  apiBaseUrl,
  token,
  tenantId,
  enabled,
  pageNumber = 1,
  pageSize = 8,
  search = '',
}: WebServiceInventoryOptions) {
  const session = useQuerySession()
  const { credential } = useApiScope({ baseUrl: apiBaseUrl, token, tenantId })
  const query = useScopedQuery({
    scope: [apiBaseUrl, tenantId],
    key: ['web-services', 'inventory', pageNumber, pageSize, search],
    keepPrevious: true,
    enabled: enabled && Boolean(tenantId),
    realtime: [{ topic: 'web-services', tenantId }],
    load: (signal) =>
      fetchWebServices(apiBaseUrl, credential(), tenantId, signal, {
        pageNumber,
        pageSize,
        search,
      }),
  })
  return {
    items: query.resource?.items ?? [],
    page: query.resource,
    status: query.status,
    error: query.error,
    refresh: query.refresh,
    isCurrentPage: query.isCurrent,
    removeItem: (id: string) =>
      session.client.setQueryData(
        query.queryKey,
        (previous: { value: NonNullable<typeof query.resource>; identity: string } | undefined) =>
          previous
            ? {
                ...previous,
                value: {
                  ...previous.value,
                  items: previous.value.items.filter((item) => item.id !== id),
                  totalCount:
                    previous.value.totalCount -
                    (previous.value.items.some((item) => item.id === id) ? 1 : 0),
                },
              }
            : previous,
      ),
  }
}
