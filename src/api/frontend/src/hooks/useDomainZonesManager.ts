import { useCallback, useEffect, useRef, useState } from 'react'
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
  const [status, setStatus] = useState<LoadStatus>(enabled ? 'loading' : 'idle')
  const [error, setError] = useState('')
  const [submitStatus, setSubmitStatus] = useState<LoadStatus>('idle')
  const [submitError, setSubmitError] = useState('')

  const controllerRef = useRef<AbortController | null>(null)
  const [scope, setScope] = useState({ apiBaseUrl, token, enabled, tenantId })

  if (scope.apiBaseUrl !== apiBaseUrl || scope.token !== token || scope.enabled !== enabled || scope.tenantId !== tenantId) {
    setScope({ apiBaseUrl, token, enabled, tenantId })
    setItems([])
    setTargets([])
    setStatus(enabled ? 'loading' : 'idle')
    setError('')
    setSubmitStatus('idle')
    setSubmitError('')
  }

  const load = useCallback((signal?: AbortSignal) => {
    return Promise.all([
      fetchDomainZones(apiBaseUrl, token, tenantId, signal),
      fetchAvailableDeploymentTargets(apiBaseUrl, undefined, token, tenantId, signal),
    ]).then(([zones, availableTargets]) => {
      if (signal?.aborted) return
      setItems(zones)
      setTargets(availableTargets)
      setStatus('success')
    }, (loadError: unknown) => {
      if (signal?.aborted) return
      setItems([])
      setTargets([])
      setStatus('error')
      setError(message(loadError, 'Unable to load domain zones.'))
    })
  }, [apiBaseUrl, token, tenantId])

  useEffect(() => {
    if (!enabled) return
    const controller = new AbortController()
    controllerRef.current = controller
    void load(controller.signal)
    return () => controller.abort()
  }, [enabled, load])

  const refresh = async () => {
    setStatus('loading')
    setError('')
    await load(controllerRef.current?.signal)
  }

  const create = async (input: { deploymentTargetId: string; label: string }) => {
    const signal = controllerRef.current?.signal
    setSubmitStatus('loading')
    setSubmitError('')
    try {
      const zone = await createDomainZone(apiBaseUrl, input, token, tenantId)
      if (signal?.aborted) return
      setItems((current) => [...current, zone])
      setSubmitStatus('success')
    } catch (submitErrorValue) {
      if (signal?.aborted) throw submitErrorValue
      setSubmitStatus('error')
      setSubmitError(message(submitErrorValue, 'Unable to reserve the domain zone.'))
      throw submitErrorValue
    }
  }

  const remove = async (id: string) => {
    const signal = controllerRef.current?.signal
    setSubmitStatus('loading')
    setSubmitError('')
    try {
      await deleteDomainZone(apiBaseUrl, id, token, tenantId)
      if (signal?.aborted) return
      setItems((current) => current.filter((zone) => zone.id !== id))
      setSubmitStatus('success')
    } catch (submitErrorValue) {
      if (signal?.aborted) throw submitErrorValue
      setSubmitStatus('error')
      setSubmitError(message(submitErrorValue, 'Unable to remove the domain zone.'))
      throw submitErrorValue
    }
  }

  return { items, targets, status, error, submitStatus, submitError, refresh, create, remove }
}
