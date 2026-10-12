import { fetchWebService } from './api'
import { useApiScope } from '../../data/useApiScope'
import type { AppServices } from '../../shell/contracts'
import { useScopedQuery } from '../../data/useScopedQuery'

export function useWebService(id: string, api: AppServices['api'], active = true) {
  const { credential } = useApiScope(api)
  const result = useScopedQuery({ scope: [api.baseUrl, api.tenantId], key: ['web-services', 'detail', id],
    active, realtime: [{ topic: 'web-services', tenantId: api.tenantId, resourceId: id }, { topic: 'operations', tenantId: api.tenantId, resourceId: id }, { topic: 'source-builds', tenantId: api.tenantId, resourceId: id }],
    load: (signal) => fetchWebService(api.baseUrl, id, credential(), api.tenantId, signal) })
  return { service: result.resource, status: result.status, error: result.error, refresh: result.refresh, refreshAfterWrite: result.refreshAfterWrite, isRefreshing: result.isRefreshing }
}
