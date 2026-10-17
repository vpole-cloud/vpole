import { useCallback, useLayoutEffect, useRef } from 'react'
import { useApiScope } from '../../data/useApiScope'
import { useProviderInvalidation } from '../../data/useProviderInvalidation'
import { useScopedQuery } from '../../data/useScopedQuery'
import { groupInterests } from '../../realtime/resourceInterests'
import { useAppServices } from '../../shell/AppContext'
import { secretInterests } from './realtime'

export function useSecretScope() {
  const { api, tenant } = useAppServices()
  const scope = useProviderInvalidation(api)
  const scopeIsCurrent = scope.isCurrent
  const permission = useRef(tenant.canManage)
  useLayoutEffect(() => {
    permission.current = tenant.canManage
  }, [tenant.canManage])
  const isCurrent = useCallback(
    () => scopeIsCurrent() && permission.current === tenant.canManage,
    [scopeIsCurrent, tenant.canManage],
  )
  return { ...scope, isCurrent, api, writeIdentity: `${scope.identity}:${tenant.canManage}` }
}
export function useSecretQuery<T>(
  key: readonly unknown[],
  load: (token: string | undefined, signal: AbortSignal) => Promise<T>,
  {
    enabled = true,
    interval = false,
    active = true,
    resourceGroupId,
  }: {
    enabled?: boolean
    interval?: number | false
    active?: boolean
    resourceGroupId?: string
  } = {},
) {
  const { api } = useAppServices()
  const origin = useApiScope(api)
  const realtime =
    key[0] === 'resource-groups'
      ? groupInterests(api.tenantId, typeof key[2] === 'string' ? key[2] : undefined)
      : key[0] === 'eligibility' && key[1] === 'deployment-targets'
        ? [
            ...groupInterests(api.tenantId, resourceGroupId),
            { topic: 'deployment-target-eligibility' as const, tenantId: api.tenantId },
          ]
        : secretInterests(api.tenantId, key)
  if (key[0] === 'secrets' && key[1] === 'projection-targets')
    realtime.push(...groupInterests(api.tenantId, resourceGroupId), {
      topic: 'deployment-target-eligibility',
      tenantId: api.tenantId,
    })
  return useScopedQuery({
    scope: [api.baseUrl, api.tenantId],
    key,
    enabled,
    interval,
    active,
    realtime,
    resourceGroupId,
    load: (signal) => load(origin.credential(), signal),
  })
}
