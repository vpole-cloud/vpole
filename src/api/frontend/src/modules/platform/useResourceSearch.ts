import { useEffect, useReducer, useState } from 'react'
import { searchResources } from '../../api'
import { useApiScope } from '../../data/useApiScope'
import { useScopedQuery } from '../../data/useScopedQuery'
import { useAppServices } from '../../shell/AppContext'
import { accumulateSearchPage, combinedSearchPage } from './searchModel'

export function useResourceSearch(query: string, pageNumber: number) {
  const { api } = useAppServices()
  const origin = useApiScope(api)
  const text = query.trim()
  const identity = `${origin.identity}:${text}`
  const [settled, setSettled] = useState('')
  useEffect(() => { const timer = window.setTimeout(() => setSettled(text), 200); return () => window.clearTimeout(timer) }, [text])
  const result = useScopedQuery({ scope: [api.baseUrl, api.tenantId], key: ['search', text, pageNumber], enabled: Boolean(text && settled === text && api.tenantId),
    load: (signal) => searchResources(api.baseUrl, text, origin.credential(), api.tenantId, signal, pageNumber) })
  const [pages, dispatch] = useReducer(accumulateSearchPage, { identity, pages: {} })
  const page = result.resource
  useEffect(() => { if (page) dispatch({ identity, page }) }, [identity, page])
  const combined = combinedSearchPage(pages, identity)
  return { result: combined ? { key: identity, page: combined } : null, key: identity, hits: combined?.items ?? [],
    status: text && settled !== text ? 'loading' : result.status, error: result.error, refresh: result.refresh }
}
