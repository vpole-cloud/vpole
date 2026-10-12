import { useEffect, useLayoutEffect, useState, type ReactNode } from 'react'
import { useQuerySession } from '../data/querySession'
import { createRealtimeConnection } from './connection'
import { RealtimeController, type CoordinationMessage } from './controller'
import { RealtimeContext } from './context'
import { createRealtimeSession, revokeRealtimeSession } from './sessionApi'
import { isPageVisible } from './usePageVisible'

export function RealtimeProvider({ baseUrl, token, enabled, children }: {
  baseUrl: string; token?: string; enabled: boolean; children: ReactNode
}) {
  const session = useQuerySession()
  return <OwnedRealtimeProvider key={`${session.id}:${baseUrl}`} baseUrl={baseUrl} token={token} enabled={enabled}>{children}</OwnedRealtimeProvider>
}

function OwnedRealtimeProvider({ baseUrl, token, enabled, children }: {
  baseUrl: string; token?: string; enabled: boolean; children: ReactNode
}) {
  const session = useQuerySession()
  const [controller] = useState(() => new RealtimeController(session, baseUrl, {
    bootstrap: (signal, credential) => createRealtimeSession(baseUrl, credential ?? '', signal),
    connection: () => createRealtimeConnection(baseUrl),
    revoke: () => revokeRealtimeSession(baseUrl),
  }))
  useLayoutEffect(() => { controller.updateCredential(token) }, [controller, token])
  useEffect(() => {
    if (!enabled || typeof BroadcastChannel === 'undefined') return
    const value = new BroadcastChannel(`pole.realtime:${baseUrl}:${session.identity}`)
    controller.setBroadcast(message => value.postMessage(message))
    value.onmessage = (event: MessageEvent<CoordinationMessage>) => {
      if (event.data && ['renewed', 'session-revoked', 'access-revoked'].includes(event.data.type) &&
        (event.data.type !== 'renewed' || isPageVisible())) controller.receive(event.data)
    }
    return () => { controller.setBroadcast(); value.close() }
  }, [controller, enabled, baseUrl, session.identity])
  useEffect(() => {
    const resume = () => { if (enabled && token && isPageVisible()) void controller.renew() }
    const suspend = () => controller.dispose()
    const visibility = () => { if (document.hidden) suspend(); else resume() }
    resume()
    document.addEventListener('visibilitychange', visibility)
    window.addEventListener('pagehide', suspend)
    window.addEventListener('pageshow', resume)
    window.addEventListener('online', resume)
    return () => {
      document.removeEventListener('visibilitychange', visibility)
      window.removeEventListener('pagehide', suspend); window.removeEventListener('pageshow', resume); window.removeEventListener('online', resume)
      controller.dispose()
    }
  }, [controller, enabled, token])
  return <RealtimeContext.Provider value={controller}>{children}</RealtimeContext.Provider>
}
