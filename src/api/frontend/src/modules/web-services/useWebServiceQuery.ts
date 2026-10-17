import { useScopedQuery } from '../../data/useScopedQuery'
import { useAppServices } from '../../shell/AppContext'
import type { RealtimeSubscription } from '../../realtime/types'

export function useWebServiceQuery<T>(
  key: readonly unknown[],
  load: (signal: AbortSignal) => Promise<T>,
  options: {
    enabled?: boolean
    active?: boolean
    interval?: number | false
    realtime?: readonly RealtimeSubscription[]
    resourceGroupId?: string
  } = {},
) {
  const { api } = useAppServices()
  return useScopedQuery({ scope: [api.baseUrl, api.tenantId], key, load, ...options })
}
