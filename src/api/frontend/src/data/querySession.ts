// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { createContext, useContext } from 'react'
import { QueryClient } from '@tanstack/react-query'

export interface ScopedQueryData<T> {
  value: T
  identity: string
}
export interface QuerySession {
  identity: string
  id: string
  client: QueryClient
  isActive: () => boolean
}
export const QuerySessionContext = createContext<QuerySession | null>(null)
export const createQueryClient = () =>
  new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
        staleTime: 0,
        gcTime: 300000,
        refetchOnWindowFocus: false,
        refetchOnMount: true,
        refetchOnReconnect: true,
        refetchIntervalInBackground: false,
      },
      mutations: { retry: false },
    },
  })
export function useQuerySession() {
  const session = useContext(QuerySessionContext)
  if (!session) throw new Error('Server queries require a session query provider.')
  return session
}
export const scopedQueryKey = (
  session: QuerySession,
  scope: readonly unknown[],
  key: readonly unknown[],
) => ['pole', session.identity, session.id, ...scope, ...key] as const
