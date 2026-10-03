import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import type { LoadStatus } from '../types'

export function useResource<T>(scopeKey: string, load: (signal: AbortSignal) => Promise<T>, refreshVersion = 0) {
  const [result, setResult] = useState<{ scopeKey: string; resource: T | null; status: LoadStatus; error: string }>(
    { scopeKey, resource: null, status: 'loading', error: '' },
  )
  const parameters = useRef({ scopeKey, load })
  useLayoutEffect(() => { parameters.current = { scopeKey, load } }, [scopeKey, load])
  const request = useRef<AbortController | null>(null)
  const mounted = useRef(false)
  const refresh = useCallback(async () => {
    if (!mounted.current) return null
    const { scopeKey, load } = parameters.current
    request.current?.abort()
    const controller = new AbortController()
    request.current = controller
    setResult((current) => ({ scopeKey, resource: current.scopeKey === scopeKey ? current.resource : null, status: 'loading', error: '' }))
    try {
      const resource = await load(controller.signal)
      if (controller.signal.aborted) return null
      setResult({ scopeKey, resource, status: 'success', error: '' })
      return resource
    } catch (failure) {
      if (!controller.signal.aborted) setResult((current) => ({ ...current, status: 'error',
        error: failure instanceof Error ? failure.message : 'Unable to load resource.' }))
      return null
    }
  }, [])
  useEffect(() => {
    let active = true
    mounted.current = true
    queueMicrotask(() => { if (active) void refresh() })
    return () => { active = false; mounted.current = false; request.current?.abort() }
  }, [refresh, scopeKey, refreshVersion])
  const current = result.scopeKey === scopeKey ? result : { resource: null, status: 'loading' as const, error: '' }
  return { ...current, refresh }
}
