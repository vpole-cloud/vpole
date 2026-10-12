import { useCallback, useEffect, useLayoutEffect, useRef } from 'react'
import { useQuery } from '@tanstack/react-query'
import type { LoadStatus } from '../types'
import { scopedQueryKey, useQuerySession } from './querySession'

interface Options<T> {
  scope: readonly unknown[]
  key: readonly unknown[]
  load: (signal: AbortSignal) => Promise<T>
  enabled?: boolean
  interval?: number | false
  keepPrevious?: boolean
}
export function useScopedQuery<T>({ scope, key, load, enabled = true, interval = false, keepPrevious = false }: Options<T>) {
  const session = useQuerySession()
  const queryKey = scopedQueryKey(session, scope, key)
  const identity = JSON.stringify(queryKey)
  const parameters = useRef({ identity, load })
  useLayoutEffect(() => { parameters.current = { identity, load } }, [identity, load])
  const query = useQuery({
    queryKey,
    enabled,
    queryFn: async ({ signal }) => {
      if (!session.isActive() || parameters.current.identity !== identity) throw new DOMException('Aborted', 'AbortError')
      const value = await parameters.current.load(signal)
      if (!session.isActive() || signal.aborted) throw new DOMException('Aborted', 'AbortError')
      return { value, identity }
    },
    // A previous page is useful only within the same authenticated tenant scope.
    placeholderData: keepPrevious ? (previous, previousQuery) =>
      JSON.stringify(previousQuery?.queryKey.slice(0, 3 + scope.length)) === JSON.stringify(queryKey.slice(0, 3 + scope.length)) ? previous : undefined : undefined,
    refetchInterval: interval,
    refetchIntervalInBackground: false,
  }, session.client)
  const mounted = useRef(false)
  useLayoutEffect(() => { mounted.current = true; return () => { mounted.current = false } }, [])
  const refetch = query.refetch
  const refresh = useCallback(async () => {
    if (!mounted.current || !session.isActive() || !enabled || parameters.current.identity !== identity) return null
    const result = await refetch({ cancelRefetch: false })
    if (!mounted.current || !session.isActive() || parameters.current.identity !== identity || result.isError) return null
    return result.data?.value ?? null
  }, [enabled, identity, refetch, session])
  const refreshAfterWrite = useCallback(async () => {
    if (!mounted.current || !session.isActive() || parameters.current.identity !== identity) return null
    await session.client.cancelQueries({ queryKey: JSON.parse(identity), exact: true })
    return refresh()
  }, [identity, refresh, session])
  useEffect(() => {
    if (enabled) return
    const queryKey = JSON.parse(identity)
    const cached = session.client.getQueryCache().find({ queryKey, exact: true })
    // Disabling one observer must not cancel a read used by another active consumer.
    if (cached && !cached.isActive()) void session.client.cancelQueries({ queryKey, exact: true })
  }, [enabled, identity, session])
  const status: LoadStatus = !enabled ? 'idle' : query.isFetching || query.isPending ? 'loading' : query.isError ? 'error' : 'success'
  return { resource: enabled ? query.data?.value ?? null : null, status,
    error: query.error instanceof Error ? query.error.message : '', refresh, refreshAfterWrite,
    isInitialLoading: enabled && query.isPending, isRefreshing: query.isFetching && !query.isPending,
    isCurrent: query.data?.identity === identity, queryKey,
  }
}
