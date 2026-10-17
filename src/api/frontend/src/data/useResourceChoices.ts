import { eligibilityKeys } from './queryKeys'
import {
  fetchAvailableComputeProfiles,
  fetchAvailableDeploymentTargets,
  fetchAvailableStorageProfiles,
  fetchIsolationScopes,
} from '../api'
import { useApiScope } from './useApiScope'
import { useScopedQuery } from './useScopedQuery'
import { useResourceGroupChoices } from '../hooks/useResourceGroupChoices'
import { groupInterests } from '../realtime/resourceInterests'

interface Selection {
  groupId?: string
  isolationScopeId?: string
  deploymentTargetId?: string
  computeProfileId?: string
  storageProfileId?: string
}

export function useResourceChoices(
  api: { baseUrl: string; tenantId?: string; token?: string },
  resourceType: string,
  selection: Selection,
  active = true,
) {
  const origin = useApiScope(api)
  const scope = [api.baseUrl, api.tenantId]
  const groups = useResourceGroupChoices({
    apiBaseUrl: api.baseUrl,
    tenantId: api.tenantId,
    token: api.token,
    enabled: Boolean(api.tenantId),
    active,
  })
  const group = selection.groupId || groups.items[0]?.id || ''
  const interest = {
    active,
    realtime: groupInterests(api.tenantId, group || undefined),
    resourceGroupId: group || undefined,
  }
  const scopesQuery = useScopedQuery({
    scope,
    key: eligibilityKeys.scopes(group),
    enabled: Boolean(group),
    ...interest,
    load: (signal) =>
      fetchIsolationScopes(api.baseUrl, group, origin.credential(), api.tenantId, signal),
  })
  const scopes = {
    ...scopesQuery,
    items: (scopesQuery.resource ?? []).filter(
      (item) => item.resourceGroupId === group && item.lifecycle === 'active',
    ),
    retry: () => {
      void scopesQuery.refresh()
    },
  }
  const isolation =
    selection.isolationScopeId || scopes.items.find((item) => item.isDefault)?.id || ''
  const targets = useScopedQuery({
    scope,
    key: eligibilityKeys.targets(resourceType, isolation, false),
    enabled: Boolean(group && isolation),
    ...interest,
    realtime: [
      ...interest.realtime,
      { topic: 'deployment-target-eligibility', tenantId: api.tenantId },
    ],
    load: (signal) =>
      fetchAvailableDeploymentTargets(
        api.baseUrl,
        isolation,
        origin.credential(),
        api.tenantId,
        signal,
        false,
        resourceType,
      ),
  })
  const target =
    selection.deploymentTargetId || (targets.resource?.length === 1 ? targets.resource[0].id : '')
  const compute = useScopedQuery({
    scope,
    key: eligibilityKeys.compute(resourceType, target),
    enabled: Boolean(target),
    ...interest,
    realtime: [...interest.realtime, { topic: 'compute-eligibility', tenantId: api.tenantId }],
    load: (signal) =>
      fetchAvailableComputeProfiles(
        api.baseUrl,
        target,
        origin.credential(),
        api.tenantId,
        signal,
        resourceType,
      ),
  })
  const storage = useScopedQuery({
    scope,
    key: eligibilityKeys.storage(resourceType, target),
    enabled: Boolean(target),
    ...interest,
    realtime: [...interest.realtime, { topic: 'storage-eligibility', tenantId: api.tenantId }],
    load: (signal) =>
      fetchAvailableStorageProfiles(
        api.baseUrl,
        resourceType,
        target,
        origin.credential(),
        api.tenantId,
        signal,
      ),
  })
  const computeProfile =
    selection.computeProfileId ||
    compute.resource?.find((item) => item.isDefault)?.id ||
    (compute.resource?.length === 1 ? compute.resource[0].id : '')
  const profiles =
    storage.resource?.filter(
      (item) =>
        !item.targetMappings ||
        item.targetMappings.some((mapping) => mapping.deploymentTargetId === target),
    ) ?? []
  const storageProfile =
    selection.storageProfileId ||
    profiles.find((item) => item.isDefault)?.id ||
    (profiles.length === 1 ? profiles[0].id : '')
  const allowed =
    [groups, scopes, targets, compute, storage].every(
      (query) => query.status === 'success' && query.isCurrent,
    ) &&
    groups.items.some((item) => item.id === group) &&
    scopes.items.some((item) => item.id === isolation) &&
    Boolean(targets.resource?.some((item) => item.id === target)) &&
    Boolean(compute.resource?.some((item) => item.id === computeProfile)) &&
    profiles.some((item) => item.id === storageProfile)
  return {
    groups,
    scopes,
    targets,
    compute,
    storage,
    group,
    isolation,
    target,
    computeProfile,
    storageProfile,
    profiles,
    allowed,
  }
}
