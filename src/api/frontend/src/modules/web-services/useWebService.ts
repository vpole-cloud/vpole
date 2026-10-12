import { fetchWebService } from '../../api'
import type { AppServices } from '../../shell/contracts'
import { useScopedQuery } from '../../data/useScopedQuery'

export function useWebService(id: string, api: AppServices['api'], refreshVersion: number) {
  const result = useScopedQuery({ scope: [api.baseUrl, api.tenantId], key: ['web-services', 'detail', id],
    load: (signal) => fetchWebService(api.baseUrl, id, api.token, api.tenantId, signal), refreshVersion })
  return { service: result.resource, status: result.status, error: result.error, refresh: result.refresh, refreshAfterWrite: result.refreshAfterWrite, isRefreshing: result.isRefreshing }
}
