import { useCallback, useLayoutEffect, useRef, useState } from 'react'

// Accepted writes can finish on the server; callbacks must stay in the originating scope.
export function useScopedMutation(scopeKey: string) {
  const [scope, setScope] = useState({ key: scopeKey, version: 0 })
  if (scope.key !== scopeKey) setScope({ key: scopeKey, version: scope.version + 1 })
  const version = scope.version
  const lifetime = useRef<{ scopeKey: string; version: number; controller: AbortController } | null>(null)
  const request = useRef<AbortController | null>(null)
  const [state, setState] = useState<{ scopeKey: string; version: number; busy: boolean; error: unknown }>({ scopeKey, version, busy: false, error: null })
  useLayoutEffect(() => {
    const controller = new AbortController()
    lifetime.current = { scopeKey, version, controller }
    return () => { controller.abort(); request.current?.abort(); request.current = null }
  }, [scopeKey, version])
  const run = useCallback(async <T,>(action: (signal: AbortSignal) => Promise<T>, accepted?: (value: T) => void | Promise<void>) => {
    const owner = lifetime.current
    if (!owner || owner.scopeKey !== scopeKey || owner.version !== version || owner.controller.signal.aborted || (request.current && !request.current.signal.aborted)) return
    const controller = new AbortController()
    request.current = controller
    const signal = AbortSignal.any([controller.signal, owner.controller.signal])
    setState({ scopeKey, version, busy: true, error: null })
    try {
      const value = await action(signal)
      if (!signal.aborted) await accepted?.(value)
      return signal.aborted ? undefined : value
    } catch (failure) {
      if (!signal.aborted) { setState({ scopeKey, version, busy: false, error: failure }); throw failure }
    } finally {
      if (request.current === controller) request.current = null
      if (!signal.aborted) setState((current) => ({ ...current, busy: false }))
    }
  }, [scopeKey, version])
  const cancel = useCallback(() => {
    if (lifetime.current?.version !== version || lifetime.current.scopeKey !== scopeKey) return
    request.current?.abort(); request.current = null; setState({ scopeKey, version, busy: false, error: null })
  }, [scopeKey, version])
  // A permission can return to the same string key while an older canceled write is held.
  // Its busy/error state belongs to that specific lifetime, never the newly authorized one.
  const current = state.scopeKey === scopeKey && state.version === version ? state : { busy: false, error: null }
  return { busy: current.busy, error: current.error, run, cancel,
    clearError: () => { if (lifetime.current?.version === version && lifetime.current.scopeKey === scopeKey) setState({ scopeKey, version, busy: false, error: null }) } }
}
