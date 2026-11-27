// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { useEffect, useLayoutEffect, useState, type ReactNode } from 'react'
import { useQuerySession } from '../data/querySession'
import { createRealtimeConnection } from './connection'
import { RealtimeController, type CoordinationMessage } from './controller'
import { RealtimeContext } from './context'
import { createRealtimeSession, revokeRealtimeSession } from './sessionApi'
import { isPageVisible } from './usePageVisible'

export function RealtimeProvider({
  baseUrl,
  token,
  enabled,
  children,
  userId,
  platformAdminClaim = false,
}: {
  baseUrl: string
  token?: string
  enabled: boolean
  children: ReactNode
  userId?: string
  platformAdminClaim?: boolean
}) {
  const session = useQuerySession()
  return (
    <OwnedRealtimeProvider
      key={`${session.id}:${baseUrl}:${userId ?? ''}`}
      baseUrl={baseUrl}
      token={token}
      enabled={enabled}
      userId={userId}
      platformAdminClaim={platformAdminClaim}
    >
      {children}
    </OwnedRealtimeProvider>
  )
}

function OwnedRealtimeProvider({
  baseUrl,
  token,
  enabled,
  children,
  userId,
  platformAdminClaim,
}: {
  baseUrl: string
  token?: string
  enabled: boolean
  children: ReactNode
  userId?: string
  platformAdminClaim: boolean
}) {
  const session = useQuerySession()
  const [controller] = useState(
    () =>
      new RealtimeController(session, baseUrl, {
        bootstrap: (signal, credential) => createRealtimeSession(baseUrl, credential ?? '', signal),
        connection: () => createRealtimeConnection(baseUrl),
        revoke: () => revokeRealtimeSession(baseUrl),
        userId,
      }),
  )
  useLayoutEffect(() => {
    controller.updateCredential(token, platformAdminClaim)
  }, [controller, token, platformAdminClaim])
  useEffect(() => {
    if (!enabled || typeof BroadcastChannel === 'undefined') return
    const value = new BroadcastChannel(`pole.realtime:${baseUrl}:${session.identity}`)
    controller.setBroadcast((message) => value.postMessage(message))
    value.onmessage = (event: MessageEvent<CoordinationMessage>) => {
      if (
        event.data &&
        ['renewed', 'session-revoked', 'access-revoked'].includes(event.data.type) &&
        (event.data.type !== 'renewed' || isPageVisible())
      )
        controller.receive(event.data)
    }
    return () => {
      controller.setBroadcast()
      value.close()
    }
  }, [controller, enabled, baseUrl, session.identity])
  useEffect(() => {
    const resume = () => {
      if (enabled && token && isPageVisible()) void controller.renew()
    }
    const suspend = () => controller.dispose()
    const visibility = () => {
      if (document.hidden) suspend()
      else resume()
    }
    resume()
    document.addEventListener('visibilitychange', visibility)
    window.addEventListener('pagehide', suspend)
    window.addEventListener('pageshow', resume)
    window.addEventListener('online', resume)
    return () => {
      document.removeEventListener('visibilitychange', visibility)
      window.removeEventListener('pagehide', suspend)
      window.removeEventListener('pageshow', resume)
      window.removeEventListener('online', resume)
      controller.dispose()
    }
  }, [controller, enabled, token])
  return <RealtimeContext.Provider value={controller}>{children}</RealtimeContext.Provider>
}
