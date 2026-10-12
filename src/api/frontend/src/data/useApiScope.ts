import { useCallback, useLayoutEffect, useMemo, useRef } from 'react'
import { scopedQueryKey, useQuerySession } from './querySession'
import { useRealtime } from '../realtime/context'

export function useApiScope(api: { baseUrl: string; tenantId?: string; token?: string; privileged?: boolean }) {
  const session = useQuerySession()
  const realtime = useRealtime()
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
  const privileged = api.privileged ?? false
  const isCurrent = useCallback(() => session.isActive() && (realtime?.allowed(api.baseUrl, api.tenantId) ?? true) &&
    (!privileged || (realtime?.platformAllowed(api.baseUrl) ?? true)) && current.current.active && current.current.identity === identity,
    [identity, session, realtime, api.baseUrl, api.tenantId, privileged])
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
  return useMemo(() => ({ session, identity, credential, isCurrent, onExit, key, privileged }), [session, identity, credential, isCurrent, onExit, key, privileged])
}
