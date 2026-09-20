import { useCallback, useEffect, useRef, useState } from 'react'
import {
  createResourceGroup,
  deleteResourceGroup,
  fetchResourceGroups,
  updateResourceGroup,
} from '../api'
import type { LoadStatus, ResourceGroup, ResourceGroupInput } from '../types'

interface UseResourceGroupsManagerOptions {
  apiBaseUrl: string
  token?: string
  tenantId?: string
  enabled: boolean
}

export function useResourceGroupsManager({
  apiBaseUrl,
  token,
  tenantId,
  enabled,
}: UseResourceGroupsManagerOptions) {
  const [items, setItems] = useState<ResourceGroup[]>([])
  const [status, setStatus] = useState<LoadStatus>('idle')
  const [error, setError] = useState('')
  const [submitStatus, setSubmitStatus] = useState<LoadStatus>('idle')
  const [submitError, setSubmitError] = useState('')
  const [deletingId, setDeletingId] = useState<string | null>(null)

  const scopeController = useRef<AbortController | null>(null)
  const listController = useRef<AbortController | null>(null)
  const pendingListChanges = useRef<Map<string, ResourceGroup | null> | null>(null)
  const [scope, setScope] = useState({ apiBaseUrl, token, tenantId, enabled })

  if (scope.apiBaseUrl !== apiBaseUrl || scope.token !== token || scope.tenantId !== tenantId || scope.enabled !== enabled) {
    setScope({ apiBaseUrl, token, tenantId, enabled })
    setItems([])
    setStatus(enabled ? 'loading' : 'idle')
    setError('')
    setSubmitStatus('idle')
    setSubmitError('')
    setDeletingId(null)
  }

  const load = useCallback(async () => {
    listController.current?.abort()
    const controller = new AbortController()
    listController.current = controller
    const changes = new Map<string, ResourceGroup | null>()
    pendingListChanges.current = changes
    setStatus('loading')
    setError('')
    try {
      const response = await fetchResourceGroups(apiBaseUrl, token, tenantId, controller.signal)
      if (controller.signal.aborted) return
      const responseIds = new Set(response.items.map((item) => item.id))
      setItems([
        ...Array.from(changes.values()).filter((item): item is ResourceGroup => item !== null && !responseIds.has(item.id)),
        ...response.items.flatMap((item) => {
          const changed = changes.has(item.id) ? changes.get(item.id) : item
          return changed ? [changed] : []
        }),
      ])
      setStatus('success')
    } catch (loadError) {
      if (controller.signal.aborted) return
      setItems([])
      setStatus('error')
      setError(loadError instanceof Error ? loadError.message : 'Unable to load resource groups.')
    } finally {
      if (pendingListChanges.current === changes) pendingListChanges.current = null
    }
  }, [apiBaseUrl, token, tenantId])

  useEffect(() => {
    if (!enabled) return
    const controller = new AbortController()
    scopeController.current = controller
    void load()
    return () => {
      controller.abort()
      listController.current?.abort()
    }
  }, [enabled, load])

  const refresh = async () => {
    if (scopeController.current?.signal.aborted || !enabled) return
    await load()
  }

  const acceptMutation = (id: string, item: ResourceGroup | null) => {
    // Retain unchanged rows from an in-flight list while applying confirmed mutations over it.
    pendingListChanges.current?.set(id, item)
    setStatus('success')
    setError('')
  }

  const create = async (input: ResourceGroupInput) => {
    const signal = scopeController.current?.signal
    setSubmitStatus('loading')
    setSubmitError('')

    try {
      const created = await createResourceGroup(apiBaseUrl, input, token, tenantId)
      if (signal?.aborted) return
      acceptMutation(created.id, created)
      setItems((current) => [created, ...current.filter((item) => item.id !== created.id)])
      setSubmitStatus('success')
    } catch (submitErrorValue) {
      if (signal?.aborted) throw submitErrorValue
      setSubmitStatus('error')
      setSubmitError(
        submitErrorValue instanceof Error
          ? submitErrorValue.message
          : 'Unable to create the resource group.',
      )
      throw submitErrorValue
    }
  }

  const update = async (id: string, input: ResourceGroupInput) => {
    const signal = scopeController.current?.signal
    setSubmitStatus('loading')
    setSubmitError('')

    try {
      const updated = await updateResourceGroup(apiBaseUrl, id, input, token, tenantId)
      if (signal?.aborted) return
      acceptMutation(updated.id, updated)
      setItems((current) =>
        current.map((item) => (item.id === id ? updated : item)),
      )
      setSubmitStatus('success')
    } catch (submitErrorValue) {
      if (signal?.aborted) throw submitErrorValue
      setSubmitStatus('error')
      setSubmitError(
        submitErrorValue instanceof Error
          ? submitErrorValue.message
          : 'Unable to update the resource group.',
      )
      throw submitErrorValue
    }
  }

  const remove = async (id: string) => {
    const signal = scopeController.current?.signal
    setDeletingId(id)
    setSubmitError('')

    try {
      await deleteResourceGroup(apiBaseUrl, id, token, tenantId)
      if (signal?.aborted) return
      acceptMutation(id, null)
      setItems((current) => current.filter((item) => item.id !== id))
    } catch (submitErrorValue) {
      if (signal?.aborted) throw submitErrorValue
      setSubmitError(
        submitErrorValue instanceof Error
          ? submitErrorValue.message
          : 'Unable to delete the resource group.',
      )
      throw submitErrorValue
    } finally {
      if (!signal?.aborted) setDeletingId(null)
    }
  }

  return {
    items,
    status,
    error,
    submitStatus,
    submitError,
    deletingId,
    refresh,
    create,
    update,
    remove,
  }
}
