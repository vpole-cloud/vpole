import { useEffect, useState } from 'react'
import {
  createDomainZone,
  deleteDomainZone,
  fetchAvailableDeploymentTargets,
  fetchDomainZones,
} from '../api'
import type { AvailableDeploymentTarget, LoadStatus, TenantDomainZone } from '../types'

interface UseDomainZonesManagerOptions {
  apiBaseUrl: string
  token?: string
  tenantId?: string
  enabled: boolean
}

const message = (error: unknown, fallback: string) =>
  error instanceof Error ? error.message : fallback

export function useDomainZonesManager({
  apiBaseUrl,
  token,
  tenantId,
  enabled,
}: UseDomainZonesManagerOptions) {
  const [items, setItems] = useState<TenantDomainZone[]>([])
  const [targets, setTargets] = useState<AvailableDeploymentTarget[]>([])
  const [status, setStatus] = useState<LoadStatus>('idle')
  const [error, setError] = useState('')
  const [submitStatus, setSubmitStatus] = useState<LoadStatus>('idle')
  const [submitError, setSubmitError] = useState('')

  const refresh = async () => {
    setStatus('loading')
    setError('')
    try {
      const [zones, availableTargets] = await Promise.all([
        fetchDomainZones(apiBaseUrl, token, tenantId),
        fetchAvailableDeploymentTargets(apiBaseUrl, undefined, token, tenantId),
      ])
      setItems(zones)
      setTargets(availableTargets)
      setStatus('success')
    } catch (loadError) {
      setItems([])
      setTargets([])
      setStatus('error')
      setError(message(loadError, 'Unable to load domain zones.'))
    }
  }

  useEffect(() => {
    if (enabled) {
      void refresh()
    }
  }, [apiBaseUrl, enabled, tenantId, token])

  const create = async (input: { deploymentTargetId: string; label: string }) => {
    setSubmitStatus('loading')
    setSubmitError('')
    try {
      const zone = await createDomainZone(apiBaseUrl, input, token, tenantId)
      setItems((current) => [...current, zone])
      setSubmitStatus('success')
    } catch (submitErrorValue) {
      setSubmitStatus('error')
      setSubmitError(message(submitErrorValue, 'Unable to reserve the domain zone.'))
      throw submitErrorValue
    }
  }

  const remove = async (id: string) => {
    setSubmitStatus('loading')
    setSubmitError('')
    try {
      await deleteDomainZone(apiBaseUrl, id, token, tenantId)
      setItems((current) => current.filter((zone) => zone.id !== id))
      setSubmitStatus('success')
    } catch (submitErrorValue) {
      setSubmitStatus('error')
      setSubmitError(message(submitErrorValue, 'Unable to remove the domain zone.'))
      throw submitErrorValue
    }
  }

  return { items, targets, status, error, submitStatus, submitError, refresh, create, remove }
}
