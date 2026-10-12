import { createContext, useContext, useEffect, useSyncExternalStore } from 'react'
import type { RealtimeController } from './controller'
import { subscriptionKey, type RealtimeSubscription } from './types'

export const RealtimeContext = createContext<RealtimeController | null>(null)
const noSubscribe = () => () => {}
const offlineSnapshot = () => 0
export function useRealtime() {
  const controller = useContext(RealtimeContext)
  useSyncExternalStore(controller?.subscribe ?? noSubscribe, controller?.getSnapshot ?? offlineSnapshot)
  return controller
}
export function useRealtimeInterest(requests: readonly RealtimeSubscription[] | undefined, active: boolean) {
  const controller = useRealtime()
  const identity = JSON.stringify(requests?.map(subscriptionKey) ?? [])
  useEffect(() => {
    if (!controller || !active) return
    const values = JSON.parse(identity) as string[]
    const releases = values.map(value => {
      const [topic, tenantId, resourceId] = JSON.parse(value) as [RealtimeSubscription['topic'], string | null, string | null]
      return controller.register({ topic, tenantId: tenantId ?? undefined, resourceId: resourceId ?? undefined })
    })
    return () => { for (const release of releases) release() }
  }, [controller, active, identity])
  return requests?.length ? requests.every(request => controller?.covered(request)) : false
}
