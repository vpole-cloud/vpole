import { useEffect, useMemo, useState } from 'react'
import { fetchTenants } from '../api'
import type { LoadStatus, Tenant } from '../types'

const storageKey = 'pole.activeTenantId'

export function useTenants(apiBaseUrl: string, token?: string, enabled = false) {
  const [items, setItems] = useState<Tenant[]>([])
  const [activeTenantId, setActiveTenantId] = useState<string>(() => localStorage.getItem(storageKey) ?? '')
  const [status, setStatus] = useState<LoadStatus>('idle')
  const [error, setError] = useState('')

  useEffect(() => {
    if (!enabled) {
      setItems([])
      setStatus('idle')
      return
    }

    const controller = new AbortController()

    const load = async () => {
      setStatus('loading')
      setError('')

      try {
        const tenants = await fetchTenants(apiBaseUrl, token, controller.signal)
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
