import { createContext, useContext } from 'react'
import { QueryClient } from '@tanstack/react-query'

export interface QuerySession { identity: string; id: string; client: QueryClient; isActive: () => boolean }
export const QuerySessionContext = createContext<QuerySession | null>(null)
export const createQueryClient = () => new QueryClient({ defaultOptions: {
  queries: { retry: false, staleTime: 0, gcTime: 300000, refetchOnWindowFocus: false },
  mutations: { retry: false },
} })
export function useQuerySession() {
  const session = useContext(QuerySessionContext)
  if (!session) throw new Error('Server queries require a session query provider.')
  return session
}
export const scopedQueryKey = (session: QuerySession, scope: readonly unknown[], key: readonly unknown[]) =>
  ['pole', session.identity, session.id, ...scope, ...key] as const
