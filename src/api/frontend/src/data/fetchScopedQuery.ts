// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import type { useApiScope } from './useApiScope'
import type { ScopedQueryData } from './querySession'
import { QueryObserver } from '@tanstack/react-query'

// Imperative reads share the declarative query's cache contract and cancellation.
export async function fetchScopedQuery<T>(
  origin: ReturnType<typeof useApiScope>,
  parts: readonly unknown[],
  load: (signal: AbortSignal) => Promise<T>,
) {
  if (!origin.isCurrent()) throw new DOMException('Aborted', 'AbortError')
  const queryKey = origin.key(parts)
  const identity = JSON.stringify(queryKey)
  const client = origin.session.client
  const query = {
    queryKey,
    meta: { privileged: origin.privileged },
    queryFn: async ({ signal }: { signal: AbortSignal }): Promise<ScopedQueryData<T>> => {
      if (!origin.session.isActive()) throw new DOMException('Aborted', 'AbortError')
      const value = await load(signal)
      if (signal.aborted || !origin.session.isActive())
        throw new DOMException('Aborted', 'AbortError')
      return { value, identity }
    },
  }
  // An imperative reader is an active owner until it completes or its scope exits.
  const observer = new QueryObserver(client, {
    ...query,
    enabled: true,
    refetchOnMount: false,
    refetchOnReconnect: false,
    refetchOnWindowFocus: false,
  })
  const unsubscribe = observer.subscribe(() => {})
  let released = false
  const release = () => {
    if (released) return
    released = true
    unsubscribe()
    const cached = client.getQueryCache().find({ queryKey, exact: true })
    if (cached && !cached.isActive()) void client.cancelQueries({ queryKey, exact: true })
  }
  const detach = origin.onExit(release)
  try {
    if (!origin.isCurrent()) throw new DOMException('Aborted', 'AbortError')
    const result = await client.fetchQuery(query)
    if (!origin.isCurrent()) throw new DOMException('Aborted', 'AbortError')
    return result.value
  } finally {
    detach()
    release()
  }
}
