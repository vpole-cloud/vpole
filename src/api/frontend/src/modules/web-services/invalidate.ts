// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import type { useApiScope } from '../../data/useApiScope'

// A write's captured origin owns every invalidation, including after tenant navigation.
export async function invalidateWebServiceState(
  scope: ReturnType<typeof useApiScope>,
  extra: readonly (readonly unknown[])[] = [],
) {
  if (!scope.isCurrent()) return
  const prefixes = [
    ['web-services'],
    ['source-builds'],
    ['resource-groups'],
    ['overview'],
    ['search'],
    ['operations'],
    ['health'],
    ['endpoints'],
    ['runtime-discovery'],
    ...extra,
  ]
  await Promise.all(
    prefixes.map(async (prefix) => {
      const queryKey = scope.key(prefix)
      await scope.session.client.cancelQueries({ queryKey })
      if (scope.isCurrent())
        await scope.session.client.invalidateQueries({ queryKey, refetchType: 'active' })
    }),
  )
}
