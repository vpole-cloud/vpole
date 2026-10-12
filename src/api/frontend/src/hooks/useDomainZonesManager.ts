import { createDomainZone, deleteDomainZone, fetchAvailableDeploymentTargets, fetchDomainZones } from '../api'
import { useApiScope } from '../data/useApiScope'
import { useScopedQuery } from '../data/useScopedQuery'
import { useScopedMutation } from './useScopedMutation'
import type { LoadStatus } from '../types'

interface UseDomainZonesManagerOptions { apiBaseUrl: string; token?: string; tenantId?: string; enabled: boolean }
export function useDomainZonesManager({ apiBaseUrl, token, tenantId, enabled }: UseDomainZonesManagerOptions) {
  const origin = useApiScope({ baseUrl: apiBaseUrl, token, tenantId })
  const zones = useScopedQuery({ scope: [apiBaseUrl, tenantId], key: ['domains', 'zones'], enabled,
    load: (signal) => fetchDomainZones(apiBaseUrl, origin.credential(), tenantId, signal) })
  const targets = useScopedQuery({ scope: [apiBaseUrl, tenantId], key: ['eligibility', 'targets', undefined, 'pole/web-service', true], enabled,
    load: (signal) => fetchAvailableDeploymentTargets(apiBaseUrl, undefined, origin.credential(), tenantId, signal) })
  const mutation = useScopedMutation(`${origin.identity}:${enabled}`)
  const refresh = async () => { await Promise.all([zones.refresh(), targets.refresh()]) }
  const changed = async () => {
    await origin.session.client.invalidateQueries({ queryKey: origin.key(['domains']), refetchType: 'active' })
    if (origin.isCurrent()) await zones.refreshAfterWrite()
  }
  const create = async (input: { deploymentTargetId: string; label: string }) => {
    if (!enabled) return
    const command = { ...input }
    await mutation.run((signal) => createDomainZone(apiBaseUrl, command, origin.credential(), tenantId, signal), changed)
  }
  const remove = async (id: string) => {
    if (!enabled) return
    await mutation.run((signal) => deleteDomainZone(apiBaseUrl, id, origin.credential(), tenantId, signal), changed)
  }
  return { items: zones.resource ?? [], targets: targets.resource ?? [],
    status: zones.status === 'success' ? targets.status : zones.status,
    error: zones.error || targets.error,
    submitStatus: (mutation.busy ? 'loading' : mutation.error ? 'error' : 'idle') as LoadStatus,
    submitError: mutation.error instanceof Error ? mutation.error.message : '', refresh, create, remove }
}
