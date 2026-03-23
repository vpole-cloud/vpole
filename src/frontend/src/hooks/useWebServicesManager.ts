import { useEffect, useState } from 'react'
import {
  createWebService,
  deleteWebService,
  fetchWebServices,
  updateWebService,
} from '../api'
import type { LoadStatus, WebService, WebServiceInput } from '../types'

interface UseWebServicesManagerOptions {
  apiBaseUrl: string
  token?: string
  enabled: boolean
}

export function useWebServicesManager({
  apiBaseUrl,
  token,
  enabled,
}: UseWebServicesManagerOptions) {
  const [items, setItems] = useState<WebService[]>([])
  const [status, setStatus] = useState<LoadStatus>('idle')
  const [error, setError] = useState('')
  const [submitStatus, setSubmitStatus] = useState<LoadStatus>('idle')
  const [submitError, setSubmitError] = useState('')
  const [deletingId, setDeletingId] = useState<string | null>(null)

  useEffect(() => {
    if (!enabled) {
      return
    }

    const load = async () => {
      setStatus('loading')
      setError('')

      try {
        const response = await fetchWebServices(apiBaseUrl, token)
        setItems(response.items)
        setStatus('success')
      } catch (loadError) {
        setItems([])
        setStatus('error')
        setError(
          loadError instanceof Error
            ? loadError.message
            : 'Unable to load web services.',
        )
      }
    }

    void load()
  }, [apiBaseUrl, enabled, token])

  const refresh = async () => {
    setStatus('loading')
    setError('')

    try {
      const response = await fetchWebServices(apiBaseUrl, token)
      setItems(response.items)
      setStatus('success')
    } catch (loadError) {
      setItems([])
      setStatus('error')
      setError(
        loadError instanceof Error
          ? loadError.message
          : 'Unable to load web services.',
      )
    }
  }

  const create = async (input: WebServiceInput) => {
    setSubmitStatus('loading')
    setSubmitError('')

    try {
      await createWebService(apiBaseUrl, input, token)
      await refresh()
      setSubmitStatus('success')
    } catch (submitErrorValue) {
      setSubmitStatus('error')
      setSubmitError(
        submitErrorValue instanceof Error
          ? submitErrorValue.message
          : 'Unable to create the web service.',
      )
      throw submitErrorValue
    }
  }

  const update = async (id: string, input: WebServiceInput) => {
    setSubmitStatus('loading')
    setSubmitError('')

    try {
      await updateWebService(apiBaseUrl, id, input, token)
      await refresh()
      setSubmitStatus('success')
    } catch (submitErrorValue) {
      setSubmitStatus('error')
      setSubmitError(
        submitErrorValue instanceof Error
          ? submitErrorValue.message
          : 'Unable to update the web service.',
      )
      throw submitErrorValue
    }
  }

  const remove = async (id: string) => {
    setDeletingId(id)
    setSubmitError('')

    try {
      await deleteWebService(apiBaseUrl, id, token)
      setItems((current) => current.filter((item) => item.id !== id))
    } catch (submitErrorValue) {
      setSubmitError(
        submitErrorValue instanceof Error
          ? submitErrorValue.message
          : 'Unable to delete the web service.',
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
