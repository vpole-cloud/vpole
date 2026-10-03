import { fetchWebServices } from '../api'
import { usePagedInventory } from './usePagedInventory'

export interface WebServiceInventoryOptions {
  apiBaseUrl: string
  token?: string
  tenantId?: string
  enabled: boolean
  pageNumber?: number
  pageSize?: number
  search?: string
}

export function useWebServiceInventory({ apiBaseUrl, token, tenantId, enabled, pageNumber = 1, pageSize = 8, search = '' }: WebServiceInventoryOptions) {
  return usePagedInventory({
    scopeKey: `${apiBaseUrl}:${tenantId}:${token}`,
    queryKey: JSON.stringify([pageNumber, pageSize, search]),
    enabled: enabled && Boolean(tenantId),
    load: (signal) => fetchWebServices(apiBaseUrl, token, tenantId, signal, { pageNumber, pageSize, search }),
    getId: (service) => service.id,
  })
}
