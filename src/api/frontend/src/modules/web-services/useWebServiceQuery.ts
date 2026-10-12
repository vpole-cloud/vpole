import { useScopedQuery } from '../../data/useScopedQuery'
import { useAppServices } from '../../shell/AppContext'
import type { RealtimeSubscription } from '../../realtime/types'

export function useWebServiceQuery<T>(key: readonly unknown[], load: (signal: AbortSignal) => Promise<T>, options: { enabled?: boolean; active?: boolean; realtime?: readonly RealtimeSubscription[] } = {}) {
  const { api } = useAppServices()
  return useScopedQuery({ scope: [api.baseUrl, api.tenantId], key, load, ...options })
}
