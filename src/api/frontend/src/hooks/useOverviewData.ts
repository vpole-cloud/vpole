import { useEffect, useState } from 'react'
import {
  fetchProtectedGreeting,
  fetchResourceGroups,
  fetchWebServices,
} from '../api'
import type { LoadStatus, OverviewData } from '../types'

interface UseOverviewDataOptions {
  apiBaseUrl: string
  token?: string
  enabled: boolean
}

const emptyOverview: OverviewData = {
  resourceGroups: [],
  webServices: [],
}

export function useOverviewData({
  apiBaseUrl,
  token,
  enabled,
}: UseOverviewDataOptions) {
  const [status, setStatus] = useState<LoadStatus>('idle')
  const [error, setError] = useState<string>('')
  const [data, setData] = useState<OverviewData>(emptyOverview)
  const [identityStatus, setIdentityStatus] = useState<LoadStatus>('idle')
  const [identityMessage, setIdentityMessage] = useState<string>('')

  useEffect(() => {
    if (!enabled) {
      return
    }

    const controller = new AbortController()

    const loadOverview = async () => {
      setStatus('loading')
      setError('')

      try {
        const [resourceGroups, webServices] = await Promise.all([
          fetchResourceGroups(apiBaseUrl, token, controller.signal),
          fetchWebServices(apiBaseUrl, token, controller.signal),
        ])

        setData({
          resourceGroups: resourceGroups.items,
          webServices: webServices.items,
        })
        setStatus('success')
      } catch (loadError) {
        if (controller.signal.aborted) {
          return
        }

        const message =
          loadError instanceof Error
            ? loadError.message
            : 'Unable to load the overview.'

        setError(message)
        setData(emptyOverview)
        setStatus('error')
      }
    }

    void loadOverview()

    return () => {
      controller.abort()
    }
  }, [apiBaseUrl, enabled, token])

  const runIdentityCheck = async () => {
    setIdentityStatus('loading')
    setIdentityMessage('')

    try {
      const message = await fetchProtectedGreeting(apiBaseUrl, token)
      setIdentityMessage(message)
      setIdentityStatus('success')
    } catch (identityError) {
      const message =
        identityError instanceof Error
          ? identityError.message
          : 'Unable to validate the current identity.'

      setIdentityMessage(message)
      setIdentityStatus('error')
    }
  }

  return {
    status,
    error,
    data,
    identityStatus,
    identityMessage,
    runIdentityCheck,
  }
}
