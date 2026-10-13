import type { LoadStatus } from '../api/types'

interface QueryDataState<T> { resource: T | null; isCurrent: boolean; status: LoadStatus; error: string }

// Metadata availability is separate from fetch activity. This does not prove
// permissions or native observation freshness; feature and command guards stay with the caller.
export function hasHealthyCurrentQueryData<T>(query: QueryDataState<T>): query is QueryDataState<T> & { resource: T } {
  return query.isCurrent && query.resource !== null && query.status !== 'error' && query.error === ''
}
