import { useCallback, useLayoutEffect, useRef, useState } from 'react'

// Accepted writes can finish on the server; callbacks must stay in the originating scope.
export function useScopedMutation(scopeKey: string) {
  const lifetime = useRef<{ scopeKey: string; controller: AbortController } | null>(null)
  const request = useRef<AbortController | null>(null)
  const [state, setState] = useState<{ scopeKey: string; busy: boolean; error: unknown }>({ scopeKey, busy: false, error: null })
  useLayoutEffect(() => {
    const controller = new AbortController()
    lifetime.current = { scopeKey, controller }
    return () => { controller.abort(); request.current?.abort(); request.current = null }
  }, [scopeKey])
  const run = useCallback(async <T,>(action: (signal: AbortSignal) => Promise<T>, accepted?: (value: T) => void | Promise<void>) => {
    const owner = lifetime.current
    if (!owner || owner.scopeKey !== scopeKey || owner.controller.signal.aborted || (request.current && !request.current.signal.aborted)) return
    const controller = new AbortController()
    request.current = controller
    const signal = AbortSignal.any([controller.signal, owner.controller.signal])
    setState({ scopeKey, busy: true, error: null })
    try {
      const value = await action(signal)
      if (!signal.aborted) await accepted?.(value)
      return signal.aborted ? undefined : value
    } catch (failure) {
      if (!signal.aborted) { setState({ scopeKey, busy: false, error: failure }); throw failure }
    } finally {
      if (request.current === controller) request.current = null
      if (!signal.aborted) setState((current) => ({ ...current, busy: false }))
    }
  }, [scopeKey])
  const cancel = useCallback(() => { request.current?.abort(); request.current = null; setState({ scopeKey, busy: false, error: null }) }, [scopeKey])
  const current = state.scopeKey === scopeKey ? state : { busy: false, error: null }
  return { ...current, run, cancel, clearError: () => setState({ scopeKey, busy: false, error: null }) }
}
