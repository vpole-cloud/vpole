// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import type { LoadStatus } from '../api/types'

interface QueryDataState<T> {
  resource: T | null
  isCurrent: boolean
  status: LoadStatus
  error: string
}

// Metadata availability is separate from fetch activity. This does not prove
// permissions or native observation freshness; feature and command guards stay with the caller.
export function hasHealthyCurrentQueryData<T>(
  query: QueryDataState<T>,
): query is QueryDataState<T> & { resource: T } {
  return (
    query.isCurrent && query.resource !== null && query.status !== 'error' && query.error === ''
  )
}
