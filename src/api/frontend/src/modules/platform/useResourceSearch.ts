import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { useInfiniteQuery } from '@tanstack/react-query'
import { searchResources } from '../../api'
import { useApiScope } from '../../data/useApiScope'
import { useRealtime, useRealtimeInterest } from '../../realtime/context'
import { isPageVisible, usePageVisible } from '../../realtime/usePageVisible'
import { useAppServices } from '../../shell/AppContext'
import type { LoadStatus } from '../../types'
import { combinedSearchPage } from './searchModel'

export function useResourceSearch(query: string, pageNumber: number) {
  const { api } = useAppServices()
  const origin = useApiScope(api)
  const controller = useRealtime()
  const visible = usePageVisible()
  const text = query.trim()
  const identity = `${origin.identity}:${text}`
  const [settled, setSettled] = useState('')
  const [readEpochs] = useState(() => new WeakMap<AbortSignal, number>())
  useEffect(() => {
    const timer = window.setTimeout(() => setSettled(text), 200)
    return () => window.clearTimeout(timer)
  }, [text])
  const allowed = controller?.allowed(api.baseUrl, api.tenantId) ?? true
  const observing = Boolean(text && settled === text && api.tenantId && visible && allowed)
  const current = useRef({ identity, observing, mounted: true })
  useLayoutEffect(() => {
    current.current = { identity, observing, mounted: true }
  }, [identity, observing])
  useLayoutEffect(
    () => () => {
      current.current.mounted = false
    },
    [],
  )
  const covered = useRealtimeInterest(
    controller && !controller.ownsApi(api.baseUrl)
      ? undefined
      : [{ topic: 'search', tenantId: api.tenantId }],
    observing,
  )
  const epoch = controller?.searchEpoch(api.baseUrl, api.tenantId) ?? 0
  const result = useInfiniteQuery(
    {
      // One cache entry owns the accumulated window. Refetch replaces every stored page,
      // including earlier pages from which a rename or deletion can remove a hit.
      queryKey: origin.key(['search', text, 'window']),
      enabled: observing,
      initialPageParam: 1,
      queryFn: async ({ signal, pageParam }) => {
        // InfiniteQuery shares one signal across a complete window refetch. A hint
        // between its pages must reject that whole read, rather than mixing epochs.
        const startedEpoch =
          readEpochs.get(signal) ?? controller?.searchEpoch(api.baseUrl, api.tenantId) ?? 0
        readEpochs.set(signal, startedEpoch)
        const page = await searchResources(
          api.baseUrl,
          text,
          origin.credential(),
          api.tenantId,
          signal,
          pageParam,
        )
        if (
          !origin.isCurrent() ||
          signal.aborted ||
          startedEpoch !== (controller?.searchEpoch(api.baseUrl, api.tenantId) ?? 0)
        )
          throw new DOMException('Aborted', 'AbortError')
        return { page, epoch: startedEpoch }
      },
      getNextPageParam: (last, pages) =>
        last.page.items.length &&
        pages.reduce((count, value) => count + value.page.items.length, 0) < last.page.totalCount
          ? last.page.pageNumber + 1
          : undefined,
      refetchInterval: covered ? 60000 : false,
      refetchIntervalInBackground: false,
    },
    origin.session.client,
  )
  const count = result.data?.pages.length ?? 0
  const fetchNextPage = result.fetchNextPage
  const hasNextPage = result.hasNextPage
  const windowCurrent = Boolean(
    result.data?.pages.length && result.data.pages.every((value) => value.epoch === epoch),
  )
  const fetching = result.isFetching
  const attempted = useRef('')
  useEffect(() => {
    const attempt = JSON.stringify([identity, epoch, pageNumber, count])
    if (
      !fetching &&
      attempted.current !== attempt &&
      observing &&
      windowCurrent &&
      epoch === (controller?.searchEpoch(api.baseUrl, api.tenantId) ?? 0) &&
      pageNumber > count &&
      pageNumber > 1 &&
      hasNextPage
    ) {
      attempted.current = attempt
      void fetchNextPage({ cancelRefetch: false })
    }
  }, [
    identity,
    observing,
    fetching,
    windowCurrent,
    epoch,
    controller,
    api.baseUrl,
    api.tenantId,
    pageNumber,
    count,
    hasNextPage,
    fetchNextPage,
  ])
  useEffect(() => {
    if (observing) return
    const key = origin.key(['search', text, 'window'])
    const cached = origin.session.client.getQueryCache().find({ queryKey: key, exact: true })
    if (cached && !cached.isActive())
      void origin.session.client.cancelQueries({ queryKey: key, exact: true })
  }, [observing, origin, text])
  const values = result.data?.pages ?? []
  // The last completed window remains useful during refresh or an HTTP failure.
  // Only a coherent full window may render; additions are gated above until current.
  const pages = values.every((value) => value.epoch === values[0]?.epoch)
    ? values.map((value) => value.page)
    : []
  const combined = allowed
    ? combinedSearchPage(
        { identity, pages: Object.fromEntries(pages.map((page) => [page.pageNumber, page])) },
        identity,
      )
    : null
  const obsolete = result.error instanceof DOMException && result.error.name === 'AbortError'
  const status: LoadStatus =
    !text || !allowed
      ? 'idle'
      : settled !== text || result.isFetching || result.isPending || obsolete
        ? 'loading'
        : result.isError
          ? 'error'
          : values.length && !windowCurrent
            ? 'loading'
            : 'success'
  const refresh = async () => {
    if (
      !current.current.mounted ||
      !current.current.observing ||
      current.current.identity !== identity ||
      !isPageVisible() ||
      !origin.isCurrent()
    )
      return
    if (
      windowCurrent &&
      epoch === (controller?.searchEpoch(api.baseUrl, api.tenantId) ?? 0) &&
      pageNumber > count &&
      hasNextPage
    )
      await fetchNextPage({ cancelRefetch: false })
    else await result.refetch({ cancelRefetch: false })
  }
  return {
    result: combined ? { key: identity, page: combined } : null,
    key: identity,
    hits: combined?.items ?? [],
    status,
    error: !obsolete && result.error instanceof Error ? result.error.message : '',
    refresh,
  }
}
