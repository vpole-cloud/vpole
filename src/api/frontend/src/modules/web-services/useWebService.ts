import { fetchWebService } from '../../api'
import type { AppServices } from '../../shell/contracts'
import { useResource } from '../../hooks/useResource'

export function useWebService(id: string, api: AppServices['api'], refreshVersion: number) {
  const result = useResource(`${api.baseUrl}:${api.tenantId}:${api.token}:${id}`,
    (signal) => fetchWebService(api.baseUrl, id, api.token, api.tenantId, signal), refreshVersion)
  return { service: result.resource, status: result.status, error: result.error, refresh: result.refresh }
}
