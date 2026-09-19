import { useEffect, useMemo, useState } from 'react'
import { fetchTenants } from '../api'
import type { LoadStatus, Tenant } from '../types'

const storageKey = 'pole.activeTenantId'

export function useTenants(apiBaseUrl: string, token?: string, enabled = false) {
  const [items, setItems] = useState<Tenant[]>([])
  const [activeTenantId, setActiveTenantId] = useState<string>(() => localStorage.getItem(storageKey) ?? '')
  const [status, setStatus] = useState<LoadStatus>(enabled ? 'loading' : 'idle')
  const [error, setError] = useState('')

  const [scope, setScope] = useState({ apiBaseUrl, token, enabled })

  // Reset before rendering a different authentication scope, not in a follow-up effect.
  if (scope.apiBaseUrl !== apiBaseUrl || scope.token !== token || scope.enabled !== enabled) {
    setScope({ apiBaseUrl, token, enabled })
    setItems([])
    setStatus(enabled ? 'loading' : 'idle')
    setError('')
  }

  useEffect(() => {
    if (!enabled) return

    const controller = new AbortController()

    const load = async () => {
      try {
        const tenants = await fetchTenants(apiBaseUrl, token, controller.signal)
        if (controller.signal.aborted) return
        setItems(tenants)
        setActiveTenantId((current) => {
          const next = tenants.some((tenant) => tenant.id === current)
            ? current
            : tenants.find((tenant) => tenant.isDefault)?.id ?? tenants[0]?.id ?? ''
          if (next) localStorage.setItem(storageKey, next)
          else localStorage.removeItem(storageKey)
          return next
        })
        setStatus('success')
      } catch (loadError) {
        if (controller.signal.aborted) return
        setItems([])
        setStatus('error')
        setError(loadError instanceof Error ? loadError.message : 'Unable to load tenants.')
      }
    }

    void load()

    return () => controller.abort()
  }, [apiBaseUrl, enabled, token])

  const activeTenant = useMemo(
    () => items.find((tenant) => tenant.id === activeTenantId) ?? null,
    [activeTenantId, items],
  )

  const selectTenant = (tenantId: string) => {
    setActiveTenantId(tenantId)
    if (tenantId) localStorage.setItem(storageKey, tenantId)
    else localStorage.removeItem(storageKey)
  }

  return { items, activeTenant, activeTenantId, status, error, selectTenant }
}
