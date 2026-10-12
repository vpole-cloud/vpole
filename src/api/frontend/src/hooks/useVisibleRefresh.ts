import { useEffect } from 'react'
import { useRealtimeInterest } from '../realtime/context'
import { usePageVisible } from '../realtime/usePageVisible'
import type { RealtimeSubscription } from '../realtime/types'

export function useVisibleRefresh(active: boolean, refresh: () => Promise<unknown>, milliseconds = 20000, realtime?: readonly RealtimeSubscription[]) {
  const visible = usePageVisible()
  const covered = useRealtimeInterest(realtime, active && visible)
  const interval = covered ? 60000 : milliseconds
  useEffect(() => {
    if (!active) return
    const reconnect = () => { if (!document.hidden) void refresh() }
    const timer = window.setInterval(reconnect, interval)
    document.addEventListener('visibilitychange', reconnect)
    window.addEventListener('online', reconnect)
    return () => { window.clearInterval(timer); document.removeEventListener('visibilitychange', reconnect); window.removeEventListener('online', reconnect) }
  }, [active, refresh, interval])
}
