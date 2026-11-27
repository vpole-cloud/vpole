// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { useCallback } from 'react'
import { useApiScope } from './useApiScope'

export function useProviderInvalidation(api: {
  baseUrl: string
  tenantId?: string
  token?: string
}) {
  const scope = useApiScope(api)
  const { key, session } = scope
  // Keys belong to this hook's origin even if its screen departs while the write completes.
  const invalidate = useCallback(
    async (prefixes: readonly (readonly unknown[])[]) => {
      if (!session.isActive()) return
      const queryKeys = prefixes.map(key)
      await Promise.all(queryKeys.map((queryKey) => session.client.cancelQueries({ queryKey })))
      await Promise.all(
        queryKeys.map((queryKey) =>
          session.client.invalidateQueries({ queryKey, refetchType: 'active' }),
        ),
      )
    },
    [key, session],
  )
  return { ...scope, invalidate }
}
