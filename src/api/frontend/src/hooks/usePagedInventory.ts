import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import type { LoadStatus, PaginatedResponse } from '../types'

interface InventoryOptions<T> {
  scopeKey: string
  queryKey: string
  enabled: boolean
  load: (signal: AbortSignal) => Promise<PaginatedResponse<T>>
  getId: (item: T) => string
}

// Shared tenant/page fencing for every provider and the group resource tree.
export function usePagedInventory<T>(options: InventoryOptions<T>) {
  const { scopeKey, queryKey, enabled } = options
  const [result, setResult] = useState<{ scopeKey: string; queryKey: string; page: PaginatedResponse<T> | null; status: LoadStatus; error: string }>(
    { scopeKey, queryKey: '', page: null, status: 'idle', error: '' },
  )
  const parameters = useRef(options)
  useLayoutEffect(() => { parameters.current = options }, [options])
  const request = useRef<AbortController | null>(null)
  const mounted = useRef(false)
  const refresh = useCallback(async () => {
    if (!mounted.current) return
    const { scopeKey, queryKey, enabled, load } = parameters.current
    request.current?.abort()
    if (!enabled) return
    const controller = new AbortController()
    request.current = controller
    setResult((current) => ({ scopeKey, queryKey: current.scopeKey === scopeKey ? current.queryKey : '',
      page: current.scopeKey === scopeKey ? current.page : null, status: 'loading', error: '' }))
    try {
      const page = await load(controller.signal)
      if (!controller.signal.aborted) setResult({ scopeKey, queryKey, page, status: 'success', error: '' })
    } catch (error) {
      if (!controller.signal.aborted) setResult((current) => ({ ...current, status: 'error',
        error: error instanceof Error ? error.message : 'Unable to load inventory.' }))
    }
  }, [])
  useEffect(() => {
    let active = true
    mounted.current = true
    queueMicrotask(() => { if (active) void refresh() })
    return () => { active = false; mounted.current = false; request.current?.abort() }
  }, [refresh, scopeKey, enabled, queryKey])
  const removeItem = (id: string) => setResult((current) => current.scopeKey !== scopeKey || !current.page ? current : {
    ...current, page: { ...current.page, items: current.page.items.filter((item) => options.getId(item) !== id),
      totalCount: Math.max(0, current.page.totalCount - (current.page.items.some((item) => options.getId(item) === id) ? 1 : 0)) },
  })
  const current = result.scopeKey === scopeKey && enabled ? result : { queryKey: '', page: null, status: 'idle' as const, error: '' }
  return { items: current.page?.items ?? [], page: current.page, isCurrentPage: current.queryKey === queryKey,
    status: current.status, error: current.error, refresh, removeItem }
}
