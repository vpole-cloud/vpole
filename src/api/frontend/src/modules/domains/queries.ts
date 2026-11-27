// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { useCallback, useLayoutEffect, useRef } from 'react'
import { useApiScope } from '../../data/useApiScope'
import { useScopedQuery } from '../../data/useScopedQuery'
import { groupInterests } from '../../realtime/resourceInterests'
import { useAppServices } from '../../shell/AppContext'
import { domainInterests } from './realtime'

export function useDomainScope() {
  const { api, tenant } = useAppServices()
  const scope = useApiScope(api)
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
export function useDomainQuery<T>(
  key: readonly unknown[],
  load: (token: string | undefined, signal: AbortSignal) => Promise<T>,
  {
    enabled = true,
    interval = false,
    active = true,
  }: { enabled?: boolean; interval?: number | false; active?: boolean } = {},
) {
  const { api } = useAppServices()
  const origin = useApiScope(api)
  const realtime =
    key[0] === 'resource-groups'
      ? groupInterests(api.tenantId, typeof key[2] === 'string' ? key[2] : undefined)
      : key[0] === 'eligibility' && key[1] === 'deployment-targets'
        ? [{ topic: 'deployment-target-eligibility' as const, tenantId: api.tenantId }]
        : domainInterests(api.tenantId)
  return useScopedQuery({
    scope: [api.baseUrl, api.tenantId],
    key,
    enabled,
    interval,
    active,
    realtime,
    load: (signal) => load(origin.credential(), signal),
  })
}
