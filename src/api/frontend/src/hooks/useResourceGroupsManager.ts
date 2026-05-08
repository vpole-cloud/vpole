import { useEffect, useState } from 'react'
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
  enabled: boolean
}

export function useResourceGroupsManager({
  apiBaseUrl,
  token,
  enabled,
}: UseResourceGroupsManagerOptions) {
  const [items, setItems] = useState<ResourceGroup[]>([])
  const [status, setStatus] = useState<LoadStatus>('idle')
  const [error, setError] = useState('')
  const [submitStatus, setSubmitStatus] = useState<LoadStatus>('idle')
  const [submitError, setSubmitError] = useState('')
  const [deletingId, setDeletingId] = useState<string | null>(null)

  useEffect(() => {
    if (!enabled) {
      return
    }

    const controller = new AbortController()

    const load = async () => {
      setStatus('loading')
      setError('')

      try {
        const response = await fetchResourceGroups(apiBaseUrl, token, controller.signal)
        setItems(response.items)
        setStatus('success')
      } catch (loadError) {
        if (controller.signal.aborted) {
          return
        }

        setItems([])
        setStatus('error')
        setError(
          loadError instanceof Error
            ? loadError.message
            : 'Unable to load resource groups.',
        )
      }
    }

    void load()

    return () => {
      controller.abort()
    }
  }, [apiBaseUrl, enabled, token])

  const refresh = async () => {
    setStatus('loading')
    setError('')

    try {
      const response = await fetchResourceGroups(apiBaseUrl, token)
      setItems(response.items)
      setStatus('success')
    } catch (loadError) {
      setItems([])
      setStatus('error')
      setError(
        loadError instanceof Error
          ? loadError.message
          : 'Unable to load resource groups.',
      )
    }
  }

  const create = async (input: ResourceGroupInput) => {
    setSubmitStatus('loading')
    setSubmitError('')

    try {
      const created = await createResourceGroup(apiBaseUrl, input, token)
      setItems((current) => [created, ...current])
      setSubmitStatus('success')
    } catch (submitErrorValue) {
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
    setSubmitStatus('loading')
    setSubmitError('')

    try {
      const updated = await updateResourceGroup(apiBaseUrl, id, input, token)
      setItems((current) =>
        current.map((item) => (item.id === id ? updated : item)),
      )
      setSubmitStatus('success')
    } catch (submitErrorValue) {
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
    setDeletingId(id)
    setSubmitError('')

    try {
      await deleteResourceGroup(apiBaseUrl, id, token)
      setItems((current) => current.filter((item) => item.id !== id))
    } catch (submitErrorValue) {
      setSubmitError(
        submitErrorValue instanceof Error
          ? submitErrorValue.message
          : 'Unable to delete the resource group.',
      )
      throw submitErrorValue
    } finally {
      setDeletingId(null)
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
