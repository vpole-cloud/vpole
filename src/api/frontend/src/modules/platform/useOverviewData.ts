import { useLayoutEffect, useState } from 'react'
import { fetchProtectedGreeting, fetchResourceGroups, fetchWebServices } from '../../api'
import { useApiScope } from '../../data/useApiScope'
import { useScopedQuery } from '../../data/useScopedQuery'
import type { LoadStatus, OverviewData } from '../../types'
import { useScopedMutation } from '../../hooks/useScopedMutation'
import { groupInterests } from '../../realtime/resourceInterests'

interface UseOverviewDataOptions {
  apiBaseUrl: string
  token?: string
  tenantId?: string
  enabled: boolean
}
const emptyOverview: OverviewData = { resourceGroups: [], webServices: [] }
export function useOverviewData({ apiBaseUrl, token, tenantId, enabled }: UseOverviewDataOptions) {
  const origin = useApiScope({ baseUrl: apiBaseUrl, token, tenantId })
  const scope = `${origin.identity}:${enabled}`
  const overview = useScopedQuery({
    scope: [apiBaseUrl, tenantId],
    key: ['overview'],
    enabled,
    interval: enabled ? 20000 : false,
    realtime: [...groupInterests(tenantId), { topic: 'web-services', tenantId }],
    load: async (signal) => {
      const [groups, services] = await Promise.all([
        fetchResourceGroups(apiBaseUrl, origin.credential(), tenantId, signal),
        fetchWebServices(apiBaseUrl, origin.credential(), tenantId, signal),
      ])
      return { resourceGroups: groups.items, webServices: services.items }
    },
  })
  const client = origin.session.client
  const queryKey = JSON.stringify(overview.queryKey)
  useLayoutEffect(() => {
    if (!enabled) void client.cancelQueries({ queryKey: JSON.parse(queryKey), exact: true })
  }, [client, enabled, queryKey])
  const mutation = useScopedMutation(scope)
  const [identity, setIdentity] = useState({ scope, message: '', status: 'idle' as LoadStatus })
  const runIdentityCheck = async () => {
    if (!enabled || !token) return
    setIdentity({ scope, message: '', status: 'loading' })
    try {
      await mutation.run(
        (signal) => fetchProtectedGreeting(apiBaseUrl, origin.credential(), signal),
        (message) => setIdentity({ scope, message, status: 'success' }),
      )
    } catch {
      if (origin.isCurrent())
        setIdentity({ scope, message: 'Unable to validate the current identity.', status: 'error' })
    }
  }
  return {
    status: overview.status,
    error: overview.error,
    data: overview.resource ?? emptyOverview,
    identityStatus: identity.scope === scope ? identity.status : ('idle' as LoadStatus),
    identityMessage: identity.scope === scope ? identity.message : '',
    runIdentityCheck,
  }
}
