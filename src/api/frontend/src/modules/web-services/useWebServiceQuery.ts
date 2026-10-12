import { useScopedQuery } from '../../data/useScopedQuery'
import { useAppServices } from '../../shell/AppContext'

export function useWebServiceQuery<T>(key: readonly unknown[], load: (signal: AbortSignal) => Promise<T>, options: { enabled?: boolean } = {}) {
  const { api } = useAppServices()
  return useScopedQuery({ scope: [api.baseUrl, api.tenantId], key, load, ...options })
}
