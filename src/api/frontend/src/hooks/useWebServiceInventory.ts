import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import { fetchWebServices } from '../api'
import type { LoadStatus, PaginatedResponse, WebService } from '../types'

export interface WebServiceInventoryOptions {
  apiBaseUrl: string
  token?: string
  tenantId?: string
  enabled: boolean
  pageNumber?: number
  pageSize?: number
  search?: string
}

export function useWebServiceInventory({ apiBaseUrl, token, tenantId, enabled, pageNumber = 1, pageSize = 8, search = '' }: WebServiceInventoryOptions) {
  const scopeKey = `${apiBaseUrl}:${tenantId}:${token}`
  const queryKey = JSON.stringify([pageNumber, pageSize, search])
  const [result, setResult] = useState<{ scopeKey: string; queryKey: string; page: PaginatedResponse<WebService> | null; status: LoadStatus; error: string }>(
    { scopeKey, queryKey: '', page: null, status: 'idle', error: '' },
  )
  const request = useRef<AbortController | null>(null)
  const mounted = useRef(false)
  const parameters = useRef({ apiBaseUrl, token, tenantId, enabled, pageNumber, pageSize, search, scopeKey, queryKey })
  useLayoutEffect(() => {
    parameters.current = { apiBaseUrl, token, tenantId, enabled, pageNumber, pageSize, search, scopeKey, queryKey }
  }, [apiBaseUrl, token, tenantId, enabled, pageNumber, pageSize, search, scopeKey, queryKey])
  const refresh = useCallback(async () => {
    if (!mounted.current) return
    // Late mutation callbacks refresh today's page, rather than their captured old query.
    const { apiBaseUrl, token, tenantId, enabled, pageNumber, pageSize, search, scopeKey, queryKey } = parameters.current
    request.current?.abort()
    if (!enabled || !tenantId) return
    const controller = new AbortController()
    request.current = controller
    setResult((current) => ({ scopeKey, queryKey: current.scopeKey === scopeKey ? current.queryKey : '',
      page: current.scopeKey === scopeKey ? current.page : null, status: 'loading', error: '' }))
    try {
      const page = await fetchWebServices(apiBaseUrl, token, tenantId, controller.signal, { pageNumber, pageSize, search })
      if (!controller.signal.aborted) setResult({ scopeKey, queryKey, page, status: 'success', error: '' })
    } catch (error) {
      if (!controller.signal.aborted) setResult((current) => ({ ...current, status: 'error',
        error: error instanceof Error ? error.message : 'Unable to load web services.' }))
    }
  }, [])
  useEffect(() => {
    let active = true
    mounted.current = true
    queueMicrotask(() => { if (active) void refresh() })
    return () => { active = false; mounted.current = false; request.current?.abort() }
  }, [refresh, scopeKey, enabled, queryKey])
  const removeItem = (id: string) => setResult((current) => current.scopeKey !== scopeKey || !current.page ? current : {
    ...current, page: { ...current.page, items: current.page.items.filter((item) => item.id !== id),
      totalCount: Math.max(0, current.page.totalCount - (current.page.items.some((item) => item.id === id) ? 1 : 0)) },
  })
  const current = result.scopeKey === scopeKey && enabled ? result : { queryKey: '', page: null, status: 'idle' as const, error: '' }
  return { items: current.page?.items ?? [], page: current.page, isCurrentPage: current.queryKey === queryKey,
    status: current.status, error: current.error, refresh, removeItem }
}
