import { useCallback, useLayoutEffect, useRef } from 'react'
import { scopedQueryKey, useQuerySession } from './querySession'

export function useApiScope(api: { baseUrl: string; tenantId?: string; token?: string }) {
  const session = useQuerySession()
  const identity = `${session.id}:${api.baseUrl}:${api.tenantId ?? ''}`
  const current = useRef({ identity, active: true, token: api.token })
  useLayoutEffect(() => {
    current.current = { identity, active: true, token: api.token }
  }, [api.token, identity])
  useLayoutEffect(() => {
    current.current.active = true
    return () => { current.current.active = false }
  }, [identity])
  const isCurrent = useCallback(() => session.isActive() && current.current.active && current.current.identity === identity, [identity, session])
  const credential = useCallback(() => {
    if (!isCurrent()) throw new DOMException('Aborted', 'AbortError')
    return current.current.token
  }, [isCurrent])
  const key = useCallback((parts: readonly unknown[]) => scopedQueryKey(session, [api.baseUrl, api.tenantId], parts), [api.baseUrl, api.tenantId, session])
  return { session, identity, credential, isCurrent, key }
}
