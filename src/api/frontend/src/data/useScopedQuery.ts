import { useCallback, useEffect, useLayoutEffect, useRef } from 'react'
import { useQuery } from '@tanstack/react-query'
import type { LoadStatus } from '../types'
import { scopedQueryKey, useQuerySession } from './querySession'
import { useRealtime, useRealtimeInterest } from '../realtime/context'
import { usePageVisible } from '../realtime/usePageVisible'
import type { RealtimeSubscription } from '../realtime/types'

interface Options<T> {
  scope: readonly unknown[]
  key: readonly unknown[]
  load: (signal: AbortSignal) => Promise<T>
  enabled?: boolean
  interval?: number | false
  keepPrevious?: boolean
  active?: boolean
  realtime?: readonly RealtimeSubscription[]
  privileged?: boolean
}
export function useScopedQuery<T>({ scope, key, load, enabled = true, interval = false, keepPrevious = false, active = true, realtime, privileged = false }: Options<T>) {
  const session = useQuerySession()
  const controller = useRealtime()
  const visible = usePageVisible()
  const [baseUrl, tenantId] = scope
  const allowed = (controller?.allowed(baseUrl, tenantId) ?? true) && (!privileged || (controller?.platformAllowed(baseUrl) ?? true))
  const observing = enabled && allowed && (!realtime || active && visible)
  const covered = useRealtimeInterest(realtime, observing)
  const queryKey = scopedQueryKey(session, scope, key)
  const identity = JSON.stringify(queryKey)
  const parameters = useRef({ identity, load, observing })
  useLayoutEffect(() => { parameters.current = { identity, load, observing } }, [identity, load, observing])
  const query = useQuery({
    queryKey,
    meta: { privileged },
    enabled: observing,
    queryFn: async ({ signal }) => {
      if (!session.isActive() || controller && (!controller.allowed(baseUrl, tenantId) || privileged && !controller.platformAllowed(baseUrl)) || parameters.current.identity !== identity) throw new DOMException('Aborted', 'AbortError')
      const value = await parameters.current.load(signal)
      if (!session.isActive() || signal.aborted) throw new DOMException('Aborted', 'AbortError')
      return { value, identity }
    },
    // A previous page is useful only within the same authenticated tenant scope.
    placeholderData: keepPrevious ? (previous, previousQuery) =>
      JSON.stringify(previousQuery?.queryKey.slice(0, 3 + scope.length)) === JSON.stringify(queryKey.slice(0, 3 + scope.length)) ? previous : undefined : undefined,
    refetchInterval: realtime && observing && covered ? 60000 : interval,
    refetchIntervalInBackground: false,
  }, session.client)
  const mounted = useRef(false)
  useLayoutEffect(() => { mounted.current = true; return () => { mounted.current = false } }, [])
  const refetch = query.refetch
  const refresh = useCallback(async () => {
    if (!mounted.current || !session.isActive() || !observing || !parameters.current.observing || parameters.current.identity !== identity || controller && (!controller.allowed(baseUrl, tenantId) || privileged && !controller.platformAllowed(baseUrl))) return null
    const result = await refetch({ cancelRefetch: false })
    if (!mounted.current || !session.isActive() || parameters.current.identity !== identity || result.isError || controller &&
      (!controller.allowed(baseUrl, tenantId) || privileged && !controller.platformAllowed(baseUrl))) return null
    return result.data?.value ?? null
  }, [observing, identity, refetch, session, controller, baseUrl, tenantId, privileged])
  const refreshAfterWrite = useCallback(async () => {
    if (!mounted.current || !session.isActive() || parameters.current.identity !== identity) return null
    await session.client.cancelQueries({ queryKey: JSON.parse(identity), exact: true })
    return refresh()
  }, [identity, refresh, session])
  useEffect(() => {
    if (observing) return
    const queryKey = JSON.parse(identity)
    const cached = session.client.getQueryCache().find({ queryKey, exact: true })
    // Disabling one observer must not cancel a read used by another active consumer.
    if (cached && !cached.isActive()) void session.client.cancelQueries({ queryKey, exact: true })
  }, [observing, identity, session])
  const status: LoadStatus = !enabled || !allowed ? 'idle' : query.isFetching || query.isPending ? 'loading' : query.isError ? 'error' : 'success'
  return { resource: enabled && allowed ? query.data?.value ?? null : null, status,
    error: query.error instanceof Error ? query.error.message : '', refresh, refreshAfterWrite,
    isInitialLoading: enabled && query.isPending, isRefreshing: query.isFetching && !query.isPending,
    isCurrent: query.data?.identity === identity, queryKey,
  }
}
