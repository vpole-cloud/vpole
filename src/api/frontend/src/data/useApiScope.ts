import { useCallback, useLayoutEffect, useMemo, useRef } from 'react'
import { scopedQueryKey, useQuerySession } from './querySession'

export function useApiScope(api: { baseUrl: string; tenantId?: string; token?: string }) {
  const session = useQuerySession()
  const identity = `${session.id}:${api.baseUrl}:${api.tenantId ?? ''}`
  const exits = useRef(new Set<() => void>())
  const current = useRef({ identity, active: true, token: api.token })
  useLayoutEffect(() => {
    current.current = { identity, active: true, token: api.token }
  }, [api.token, identity])
  useLayoutEffect(() => {
    const callbacks = exits.current
    current.current.active = true
    return () => {
      current.current.active = false
      for (const exit of callbacks) exit()
      callbacks.clear()
    }
  }, [identity])
  const isCurrent = useCallback(() => session.isActive() && current.current.active && current.current.identity === identity, [identity, session])
  const onExit = useCallback((exit: () => void) => {
    if (!isCurrent()) { exit(); return () => {} }
    const callbacks = exits.current
    callbacks.add(exit)
    return () => { callbacks.delete(exit) }
  }, [isCurrent])
  const credential = useCallback(() => {
    if (!isCurrent()) throw new DOMException('Aborted', 'AbortError')
    return current.current.token
  }, [isCurrent])
  const key = useCallback((parts: readonly unknown[]) => scopedQueryKey(session, [api.baseUrl, api.tenantId], parts), [api.baseUrl, api.tenantId, session])
  return useMemo(() => ({ session, identity, credential, isCurrent, onExit, key }), [session, identity, credential, isCurrent, onExit, key])
}
