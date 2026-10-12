import { useState } from 'react'
import { createResourceGroup, deleteResourceGroup, fetchResourceGroups, updateResourceGroup } from '../../api'
import { useResourceGroupChoices } from '../../hooks/useResourceGroupChoices'
import { useScopedMutation } from '../../hooks/useScopedMutation'
import { useProviderInvalidation } from '../../data/useProviderInvalidation'
import { useScopedQuery } from '../../data/useScopedQuery'
import type { ResourceGroupInput } from './types'

type Api = { baseUrl: string; token?: string; tenantId?: string }
export function useResourceGroupInventory(api: Api, pageNumber: number, pageSize: number, search = '', enabled = true, refreshVersion = 0) {
  const origin = useProviderInvalidation(api)
  const query = useScopedQuery({ scope: [api.baseUrl, api.tenantId], key: ['resource-groups', 'inventory', pageNumber, pageSize, search],
    keepPrevious: true, enabled: enabled && Boolean(api.tenantId), refreshVersion,
    load: (signal) => fetchResourceGroups(api.baseUrl, origin.credential(), api.tenantId, signal, { pageNumber, pageSize, search }) })
  return { ...query, items: query.resource?.items ?? [], page: query.resource, isCurrentPage: query.isCurrent }
}

export function useResourceGroupsManager({ apiBaseUrl, token, tenantId, enabled, loadInventory = true }:
  { apiBaseUrl: string; token?: string; tenantId?: string; enabled: boolean; loadInventory?: boolean }) {
  const api = { baseUrl: apiBaseUrl, token, tenantId }
  const origin = useProviderInvalidation(api)
  const choices = useResourceGroupChoices({ apiBaseUrl, token, tenantId, enabled: enabled && loadInventory })
  const mutation = useScopedMutation(`${origin.identity}:${enabled}`)
  const [completed, setCompleted] = useState<string | null>(null)
  const [deletingId, setDeletingId] = useState<string | null>(null)
  const prefixes = [['resource-groups'], ['overview'], ['search'], ['operations']] as const
  const create = (input: ResourceGroupInput) => mutation.run(async (signal) => {
    if (!enabled) throw new DOMException('Aborted', 'AbortError')
    const created = await createResourceGroup(apiBaseUrl, input, origin.credential(), tenantId, signal)
    await origin.invalidate(prefixes)
    return created
  }, () => { if (origin.isCurrent()) setCompleted(origin.identity) })
  const update = (id: string, input: ResourceGroupInput) => mutation.run(async (signal) => {
    if (!enabled) throw new DOMException('Aborted', 'AbortError')
    const updated = await updateResourceGroup(apiBaseUrl, id, input, origin.credential(), tenantId, signal)
    await origin.invalidate(prefixes)
    return updated
  }, () => { if (origin.isCurrent()) setCompleted(origin.identity) })
  const remove = async (id: string) => {
    setDeletingId(id)
    try {
      return await mutation.run(async (signal) => {
        if (!enabled) throw new DOMException('Aborted', 'AbortError')
        await deleteResourceGroup(apiBaseUrl, id, origin.credential(), tenantId, signal)
        await origin.invalidate(prefixes)
        return true
      }, () => { if (origin.isCurrent()) setCompleted(origin.identity) })
    } finally { if (origin.isCurrent()) setDeletingId(null) }
  }
  return { items: choices.items, status: choices.status, error: choices.error, refresh: choices.refresh,
    submitStatus: mutation.busy ? 'loading' as const : mutation.error ? 'error' as const : completed === origin.identity ? 'success' as const : 'idle' as const,
    submitError: mutation.error instanceof Error ? mutation.error.message : '', deletingId, create, update, remove }
}
