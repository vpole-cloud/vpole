// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { useContext, useLayoutEffect, useRef, useState, type ReactNode } from 'react'
import { QueryClientProvider } from '@tanstack/react-query'
import { createQueryClient, QuerySessionContext, type QuerySession } from './querySession'

function OwnedSession({ identity, children }: { identity: string; children: ReactNode }) {
  const active = useRef(true)
  const [session] = useState<QuerySession>(() => ({
    identity,
    id: crypto.randomUUID(),
    client: createQueryClient(),
    isActive: () => active.current,
  }))
  useLayoutEffect(() => {
    active.current = true
    return () => {
      active.current = false
      void session.client.cancelQueries()
      session.client.clear()
    }
  }, [session])
  return (
    <QuerySessionContext.Provider value={session}>
      <QueryClientProvider client={session.client}>{children}</QueryClientProvider>
    </QuerySessionContext.Provider>
  )
}
export function SessionQueries({ identity, children }: { identity: string; children: ReactNode }) {
  const parent = useContext(QuerySessionContext)
  return parent?.identity === identity ? (
    children
  ) : (
    <OwnedSession key={identity} identity={identity}>
      {children}
    </OwnedSession>
  )
}
