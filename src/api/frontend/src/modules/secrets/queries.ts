import { useCallback, useLayoutEffect, useRef } from 'react'
import { useApiScope } from '../../data/useApiScope'
import { useScopedQuery } from '../../data/useScopedQuery'
import { useAppServices } from '../../shell/AppContext'

export function useSecretScope() {
  const { api, tenant } = useAppServices()
  const scope = useApiScope(api)
  const scopeIsCurrent = scope.isCurrent
  const permission = useRef(tenant.canManage)
  useLayoutEffect(() => { permission.current = tenant.canManage }, [tenant.canManage])
  const isCurrent = useCallback(() => scopeIsCurrent() && permission.current === tenant.canManage, [scopeIsCurrent, tenant.canManage])
  return { ...scope, isCurrent, api, writeIdentity: `${scope.identity}:${tenant.canManage}` }
}
export function useSecretQuery<T>(key: readonly unknown[], load: (token: string | undefined, signal: AbortSignal) => Promise<T>, refreshVersion = 0, enabled = true, interval: number | false = false) {
  const { api } = useAppServices()
  const origin = useApiScope(api)
  return useScopedQuery({ scope: [api.baseUrl, api.tenantId], key, enabled, refreshVersion, interval,
    load: (signal) => load(origin.credential(), signal) })
}
