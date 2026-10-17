import { useEffect, useMemo, useRef, useState } from 'react'
import { fetchTenants } from '../api'
import { useScopedQuery } from '../data/useScopedQuery'
import { useQuerySession } from '../data/querySession'
import { useRealtime } from '../realtime/context'

const storageKey = 'pole.activeTenantId'
const storedTenant = () => {
  try {
    return localStorage.getItem(storageKey) ?? ''
  } catch {
    return ''
  }
}
const storeTenant = (id: string) => {
  try {
    if (id) localStorage.setItem(storageKey, id)
    else localStorage.removeItem(storageKey)
  } catch {
    /* Storage is optional. */
  }
}

export function useTenants(apiBaseUrl: string, token?: string, enabled = false, userId = '') {
  const session = useQuerySession()
  const realtime = useRealtime()
  const membership = realtime?.ownsApi(apiBaseUrl) ? realtime : null
  const [preferredTenantId, setActiveTenantId] = useState(storedTenant)
  const query = useScopedQuery({
    scope: [apiBaseUrl, userId],
    key: ['tenants'],
    enabled: enabled && (!membership || Boolean(userId)),
    authorizationScope: 'user',
    interval: 60000,
    realtime: userId ? [{ topic: 'tenant-access', resourceId: userId }] : undefined,
    load: async (signal) => {
      const epoch = membership?.beginMembershipRead(apiBaseUrl, userId)
      const tenants = await fetchTenants(apiBaseUrl, token, signal)
      if (
        signal.aborted ||
        !session.isActive() ||
        (membership &&
          !membership.acceptMembershipSnapshot(
            apiBaseUrl,
            userId,
            epoch!,
            tenants.map((tenant) => tenant.id),
            signal,
          ))
      )
        throw new DOMException('Aborted', 'AbortError')
      return tenants
    },
  })
  const previousCredential = useRef(token)
  const queryIdentity = JSON.stringify(query.queryKey)
  const refresh = query.refresh
  useEffect(() => {
    if (previousCredential.current === token) return
    previousCredential.current = token
    if (!enabled) return
    let active = true
    // Credential rotation revalidates membership without removing usable discovery data.
    void session.client
      .cancelQueries({ queryKey: JSON.parse(queryIdentity), exact: true })
      .then(() => {
        if (active) void refresh()
      })
    return () => {
      active = false
    }
  }, [token, enabled, session, queryIdentity, refresh])
  const items = useMemo(() => query.resource ?? [], [query.resource])
  const activeTenantId = items.some((tenant) => tenant.id === preferredTenantId)
    ? preferredTenantId
    : (items.find((tenant) => tenant.isDefault)?.id ?? items[0]?.id ?? '')
  useEffect(() => {
    if (enabled && query.resource) storeTenant(activeTenantId)
  }, [activeTenantId, enabled, query.resource])
  const activeTenant = useMemo(
    () => items.find((tenant) => tenant.id === activeTenantId) ?? null,
    [activeTenantId, items],
  )
  return {
    items,
    activeTenant,
    activeTenantId,
    status: query.status,
    error: query.error,
    selectTenant: (id: string) => {
      setActiveTenantId(id)
      storeTenant(id)
    },
  }
}
