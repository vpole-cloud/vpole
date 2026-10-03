import { useCallback, useEffect, useRef, useState } from 'react'
import { fetchWebService } from '../../api'
import type { AppServices } from '../../shell/contracts'
import type { LoadStatus, WebService } from '../../types'

export function useWebService(id: string, api: AppServices['api'], refreshVersion: number) {
  const [service, setService] = useState<WebService | null>(null)
  const [status, setStatus] = useState<LoadStatus>('loading')
  const [error, setError] = useState('')
  const pending = useRef<AbortController | null>(null)
  const mounted = useRef(false)
  const { baseUrl, token, tenantId } = api
  const refresh = useCallback(async () => {
    if (!mounted.current) return null
    pending.current?.abort()
    const controller = new AbortController()
    pending.current = controller
    setStatus('loading')
    setError('')
    try {
      const result = await fetchWebService(baseUrl, id, token, tenantId, controller.signal)
      if (controller.signal.aborted) return null
      setService(result)
      setStatus('success')
      return result
    } catch (failure) {
      if (!controller.signal.aborted) {
        setError(failure instanceof Error ? failure.message : 'Unable to load the service.')
        setStatus('error')
      }
      return null
    }
  }, [baseUrl, id, tenantId, token])
  useEffect(() => {
    mounted.current = true
    queueMicrotask(() => { if (mounted.current) void refresh() })
    return () => {
      mounted.current = false
      pending.current?.abort()
    }
  }, [refresh, refreshVersion])
  return { service, status, error, refresh }
}
