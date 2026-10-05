import { useEffect } from 'react'

export function useVisibleRefresh(active: boolean, refresh: () => Promise<unknown>, milliseconds = 20000) {
  useEffect(() => {
    if (!active) return
    const reconnect = () => { if (!document.hidden) void refresh() }
    const timer = window.setInterval(reconnect, milliseconds)
    document.addEventListener('visibilitychange', reconnect)
    window.addEventListener('online', reconnect)
    return () => { window.clearInterval(timer); document.removeEventListener('visibilitychange', reconnect); window.removeEventListener('online', reconnect) }
  }, [active, refresh, milliseconds])
}
