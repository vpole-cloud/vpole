import { useEffect, useState } from 'react'
import type { IsolationScope, LoadStatus } from '../types'

export type LoadIsolationScopes = (resourceGroupId: string, signal?: AbortSignal) => Promise<IsolationScope[]>

export function useIsolationScopes(resourceGroupId: string, load?: LoadIsolationScopes) {
  const [state, setState] = useState<{ groupId: string; items: IsolationScope[]; status: LoadStatus; error: string }>(
    { groupId: '', items: [], status: 'idle', error: '' },
  )
  const [retryVersion, setRetryVersion] = useState(0)
  useEffect(() => {
    if (!resourceGroupId || !load) return
    const controller = new AbortController()
    queueMicrotask(async () => {
      if (controller.signal.aborted) return
      setState({ groupId: resourceGroupId, items: [], status: 'loading', error: '' })
      try {
        const items = await load(resourceGroupId, controller.signal)
        if (!controller.signal.aborted) setState({ groupId: resourceGroupId, items, status: 'success', error: '' })
      } catch (error) {
        if (!controller.signal.aborted) setState({ groupId: resourceGroupId, items: [], status: 'error',
          error: error instanceof Error ? error.message : 'Unable to load isolation scopes.' })
      }
    })
    return () => controller.abort()
  }, [resourceGroupId, load, retryVersion])
  const current = state.groupId === resourceGroupId ? state : {
    items: [], status: resourceGroupId && load ? 'loading' as const : 'idle' as const, error: '',
  }
  return { ...current, items: current.items.filter((scope) => scope.resourceGroupId === resourceGroupId && scope.lifecycle === 'active'),
    retry: () => setRetryVersion((version) => version + 1) }
}
