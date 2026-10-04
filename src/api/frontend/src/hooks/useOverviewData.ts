import { useState } from 'react'
import { fetchProtectedGreeting, fetchResourceGroups, fetchWebServices } from '../api'
import type { LoadStatus, OverviewData } from '../types'
import { useResource } from './useResource'
import { useScopedMutation } from './useScopedMutation'
import { useVisibleRefresh } from './useVisibleRefresh'

interface UseOverviewDataOptions { apiBaseUrl: string; token?: string; tenantId?: string; enabled: boolean }
const emptyOverview: OverviewData = { resourceGroups: [], webServices: [] }

export function useOverviewData({ apiBaseUrl, token, tenantId, enabled }: UseOverviewDataOptions) {
  const scope = `${apiBaseUrl}:${token}:${tenantId}:${enabled}`
  const overview = useResource(scope, async (signal) => {
    if (!enabled) return emptyOverview
    const [groups, services] = await Promise.all([
      fetchResourceGroups(apiBaseUrl, token, tenantId, signal), fetchWebServices(apiBaseUrl, token, tenantId, signal),
    ])
    return { resourceGroups: groups.items, webServices: services.items }
  })
  useVisibleRefresh(enabled, overview.refresh)
  const mutation = useScopedMutation(scope)
  const [identity, setIdentity] = useState({ scope, message: '', status: 'idle' as LoadStatus })
  const runIdentityCheck = async () => {
    if (!enabled || !token) return
    setIdentity({ scope, message: '', status: 'loading' })
    try {
      await mutation.run((signal) => fetchProtectedGreeting(apiBaseUrl, token, signal), (message) => setIdentity({ scope, message, status: 'success' }))
    } catch { setIdentity({ scope, message: 'Unable to validate the current identity.', status: 'error' }) }
  }
  return {
    status: enabled ? overview.status : 'idle' as LoadStatus,
    error: enabled ? overview.error : '',
    data: enabled ? overview.resource ?? emptyOverview : emptyOverview,
    identityStatus: identity.scope === scope ? identity.status : 'idle' as LoadStatus,
    identityMessage: identity.scope === scope ? identity.message : '',
    runIdentityCheck,
  }
}
