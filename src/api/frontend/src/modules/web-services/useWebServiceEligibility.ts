// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { useCallback, useEffect, useState } from 'react'
import { useQueries } from '@tanstack/react-query'
import { fetchAvailableComputeProfiles, fetchAvailableDeploymentTargets } from '../../api'
import { fetchScopedQuery } from '../../data/fetchScopedQuery'
import { eligibilityKeys } from '../../data/queryKeys'
import type { ScopedQueryData } from '../../data/querySession'
import { useApiScope } from '../../data/useApiScope'
import { useScopedQuery } from '../../data/useScopedQuery'
import { useRealtime, useRealtimeInterest } from '../../realtime/context'
import { groupInterests } from '../../realtime/resourceInterests'
import { isPageVisible, usePageVisible } from '../../realtime/usePageVisible'
import type { AvailableComputeProfile, LoadStatus } from '../../types'

// The form may select several targets. Keep each requested profile query observed while
// choices are visible, rather than copying completed imperative reads into local arrays.
export function useWebServiceEligibility(
  api: { baseUrl: string; tenantId?: string; token?: string },
  active: boolean,
  resourceGroupId?: string,
) {
  const origin = useApiScope(api)
  const { credential, isCurrent } = origin
  const identity = `${origin.identity}:${resourceGroupId ?? ''}`
  const [selection, setSelection] = useState<{
    identity: string
    scopeId?: string
    publicIngress: boolean
  }>()
  const [requested, setRequested] = useState<{ identity: string; ids: string[] }>({
    identity,
    ids: [],
  })
  const interests = groupInterests(api.tenantId, resourceGroupId)
  const visible = usePageVisible()
  const enabled = active && visible && Boolean(api.tenantId) && isCurrent()
  const currentSelection = selection?.identity === identity ? selection : undefined
  const targets = useScopedQuery({
    scope: [api.baseUrl, api.tenantId],
    key: eligibilityKeys.targets(
      'pole/web-service',
      currentSelection?.scopeId,
      currentSelection?.publicIngress ?? true,
    ),
    enabled: Boolean(currentSelection && api.tenantId),
    active,
    realtime: [...interests, { topic: 'deployment-target-eligibility', tenantId: api.tenantId }],
    resourceGroupId,
    load: (signal) =>
      fetchAvailableDeploymentTargets(
        api.baseUrl,
        currentSelection?.scopeId,
        credential(),
        api.tenantId,
        signal,
        currentSelection?.publicIngress ?? true,
      ),
  })
  const ids = requested.identity === identity ? requested.ids : []
  const realtime = useRealtime()
  const covered = useRealtimeInterest(
    realtime && !realtime.ownsApi(api.baseUrl)
      ? undefined
      : [...interests, { topic: 'compute-eligibility', tenantId: api.tenantId }],
    enabled && ids.length > 0,
  )
  const profiles = useQueries(
    {
      queries: ids.map((targetId) => {
        const queryKey = origin.key(eligibilityKeys.compute('pole/web-service', targetId))
        const queryIdentity = JSON.stringify(queryKey)
        return {
          queryKey,
          meta: { resourceGroupId, authorizationScope: 'tenant' },
          enabled,
          refetchInterval: covered ? 60000 : (false as const),
          refetchIntervalInBackground: false,
          queryFn: async ({
            signal,
          }: {
            signal: AbortSignal
          }): Promise<ScopedQueryData<AvailableComputeProfile[]>> => {
            if (!isCurrent()) throw new DOMException('Aborted', 'AbortError')
            const value = await fetchAvailableComputeProfiles(
              api.baseUrl,
              targetId,
              credential(),
              api.tenantId,
              signal,
            )
            if (signal.aborted || !isCurrent()) throw new DOMException('Aborted', 'AbortError')
            return { value, identity: queryIdentity }
          },
        }
      }),
    },
    origin.session.client,
  )
  const queryIdentities = JSON.stringify(
    ids.map((id) => origin.key(eligibilityKeys.compute('pole/web-service', id))),
  )
  useEffect(() => {
    if (enabled) return
    for (const queryKey of JSON.parse(queryIdentities) as unknown[][]) {
      const query = origin.session.client.getQueryCache().find({ queryKey, exact: true })
      if (query && !query.isActive())
        void origin.session.client.cancelQueries({ queryKey, exact: true })
    }
  }, [enabled, queryIdentities, origin.session])
  const loadAvailableTargets = useCallback(
    async (scopeId?: string, publicIngress = true, signal?: AbortSignal) => {
      if (!active || !isPageVisible() || !api.tenantId || signal?.aborted || !isCurrent()) return []
      setSelection({ identity, scopeId, publicIngress })
      try {
        const value = await fetchScopedQuery(
          origin,
          eligibilityKeys.targets('pole/web-service', scopeId, publicIngress),
          (querySignal) =>
            fetchAvailableDeploymentTargets(
              api.baseUrl,
              scopeId,
              credential(),
              api.tenantId,
              querySignal,
              publicIngress,
            ),
        )
        return signal?.aborted || !isCurrent() ? [] : value
      } catch {
        return []
      }
    },
    [active, api.baseUrl, api.tenantId, identity, origin, credential, isCurrent],
  )
  const loadComputeProfiles = useCallback(
    async (targetId: string) => {
      if (!active || !isPageVisible() || !api.tenantId || !targetId || !isCurrent()) return []
      setRequested((current) => ({
        identity,
        ids: [...new Set([...(current.identity === identity ? current.ids : []), targetId])],
      }))
      try {
        return await fetchScopedQuery(
          origin,
          eligibilityKeys.compute('pole/web-service', targetId),
          (signal) =>
            fetchAvailableComputeProfiles(
              api.baseUrl,
              targetId,
              credential(),
              api.tenantId,
              signal,
            ),
        )
      } catch {
        return []
      }
    },
    [active, api.baseUrl, api.tenantId, identity, origin, credential, isCurrent],
  )
  return {
    availableTargets: targets.resource ?? [],
    targetsStatus: targets.status,
    targetsError: targets.error,
    loadAvailableTargets,
    loadComputeProfiles,
    computeProfilesByTarget: Object.fromEntries(
      ids.map((id, index) => [id, isCurrent() ? (profiles[index].data?.value ?? []) : []]),
    ),
    computeProfilesStatusByTarget: Object.fromEntries(
      ids.map((id, index) => [
        id,
        !isCurrent()
          ? 'idle'
          : profiles[index].isFetching || profiles[index].isPending
            ? 'loading'
            : profiles[index].isError
              ? 'error'
              : 'success',
      ]),
    ) as Record<string, LoadStatus>,
  }
}
