import { useCallback, useState } from 'react'
import {
  addWebServiceDeployment,
  addWebServiceHostnameBinding,
  createWebService,
  deleteWebService,
  deleteWebServiceDeployment,
  deleteWebServiceHostnameBinding,
  fetchAvailableComputeProfiles,
  fetchAvailableDeploymentTargets,
  fetchWebServiceHostnameBindings,
  fetchWebServiceReleases,
  retryWebServiceDeployment,
  updateWebService,
  updateWebServiceDeployment,
} from '../api'
import type {
  AvailableComputeProfile,
  AvailableDeploymentTarget,
  HostnameBinding,
  LoadStatus,
  WebServiceDeploymentInput,
  WebServiceDeploymentUpdateInput,
  WebServiceInput,
  WebServiceReleaseHistory,
} from '../types'
import { useWebServiceInventory } from './useWebServiceInventory'

interface UseWebServicesManagerOptions {
  apiBaseUrl: string
  token?: string
  tenantId?: string
  enabled: boolean
  refreshAfterMutation?: boolean
  pageNumber?: number
  pageSize?: number
  search?: string
}

const errorMessage = (error: unknown, fallback: string) =>
  error instanceof Error ? error.message : fallback

export function useWebServicesManager({
  apiBaseUrl,
  token,
  tenantId,
  enabled,
  refreshAfterMutation = true,
  pageNumber,
  pageSize,
  search,
}: UseWebServicesManagerOptions) {
  const { items, page, isCurrentPage, status, error, refresh, removeItem } = useWebServiceInventory({ apiBaseUrl, token, tenantId, enabled, pageNumber, pageSize, search })
  const [submitStatus, setSubmitStatus] = useState<LoadStatus>('idle')
  const [submitError, setSubmitError] = useState('')
  const [deletingId, setDeletingId] = useState<string | null>(null)
  const [availableTargets, setAvailableTargets] = useState<AvailableDeploymentTarget[]>([])
  const [targetsStatus, setTargetsStatus] = useState<LoadStatus>('idle')
  const [targetsError, setTargetsError] = useState('')
  const [computeProfilesByTarget, setComputeProfilesByTarget] = useState<
    Record<string, AvailableComputeProfile[]>
  >({})
  const [hostnameBindingsByDeployment, setHostnameBindingsByDeployment] = useState<
    Record<string, HostnameBinding[]>
  >({})
  const [releasesByWebService, setReleasesByWebService] = useState<
    Record<string, WebServiceReleaseHistory>
  >({})

  const loadAvailableTargets = useCallback(async (
    isolationScopeId?: string,
    requiresPublicIngress = true,
    signal?: AbortSignal,
  ) => {
    if (!tenantId) {
      setAvailableTargets([])
      setTargetsStatus('idle')
      return []
    }

    setTargetsStatus('loading')
    setTargetsError('')
    try {
      const targets = await fetchAvailableDeploymentTargets(
        apiBaseUrl,
        isolationScopeId,
        token,
        tenantId,
        signal,
        requiresPublicIngress,
      )
      if (signal?.aborted) return []
      setAvailableTargets(targets)
      setTargetsStatus('success')
      return targets
    } catch (loadError) {
      if (signal?.aborted) return []
      setAvailableTargets([])
      setTargetsStatus('error')
      setTargetsError(errorMessage(loadError, 'Unable to load eligible deployment targets.'))
      return []
    }
  }, [apiBaseUrl, tenantId, token])

  const loadComputeProfiles = useCallback(async (deploymentTargetId: string) => {
    if (!tenantId || !deploymentTargetId) {
      return []
    }

    try {
      const profiles = await fetchAvailableComputeProfiles(
        apiBaseUrl,
        deploymentTargetId,
        token,
        tenantId,
      )
      setComputeProfilesByTarget((current) => ({
        ...current,
        [deploymentTargetId]: profiles,
      }))
      return profiles
    } catch (loadError) {
      setSubmitError(errorMessage(loadError, 'Unable to load compute profiles.'))
      return []
    }
  }, [apiBaseUrl, tenantId, token])

  const loadHostnameBindings = useCallback(async (webServiceId: string, deploymentId: string) => {
    try {
      const bindings = await fetchWebServiceHostnameBindings(
        apiBaseUrl,
        webServiceId,
        deploymentId,
        token,
        tenantId,
      )
      setHostnameBindingsByDeployment((current) => ({ ...current, [deploymentId]: bindings }))
      return bindings
    } catch (loadError) {
      setSubmitError(errorMessage(loadError, 'Unable to load hostname bindings.'))
      return []
    }
  }, [apiBaseUrl, tenantId, token])

  const loadReleases = useCallback(async (webServiceId: string) => {
    try {
      const history = await fetchWebServiceReleases(apiBaseUrl, webServiceId, token, tenantId)
      setReleasesByWebService((current) => ({ ...current, [webServiceId]: history }))
      return history
    } catch (loadError) {
      setSubmitError(errorMessage(loadError, 'Unable to load release history.'))
      return undefined
    }
  }, [apiBaseUrl, tenantId, token])

  const submit = async <T,>(action: () => Promise<T>, fallback: string) => {
    setSubmitStatus('loading')
    setSubmitError('')
    try {
      const result = await action()
      if (refreshAfterMutation) await refresh()
      setSubmitStatus('success')
      return result
    } catch (submitErrorValue) {
      setSubmitStatus('error')
      setSubmitError(errorMessage(submitErrorValue, fallback))
      throw submitErrorValue
    }
  }

  const create = async (input: WebServiceInput) => {
    return submit(
      () => createWebService(apiBaseUrl, input, token, tenantId),
      'Unable to create the web service.',
    )
  }

  const update = async (id: string, input: WebServiceInput) => {
    await submit(
      () => updateWebService(apiBaseUrl, id, input, token, tenantId),
      'Unable to update the web service.',
    )
  }

  const addDeployment = (id: string, input: WebServiceDeploymentInput) =>
    submit(
      () => addWebServiceDeployment(apiBaseUrl, id, input, token, tenantId),
      'Unable to add the deployment target.',
    )

  const updateDeployment = (
    id: string,
    deploymentId: string,
    input: WebServiceDeploymentUpdateInput,
  ) =>
    submit(
      () => updateWebServiceDeployment(apiBaseUrl, id, deploymentId, input, token, tenantId),
      'Unable to update the deployment target.',
    )

  const removeDeployment = (id: string, deploymentId: string) =>
    submit(
      () => deleteWebServiceDeployment(apiBaseUrl, id, deploymentId, token, tenantId),
      'Unable to remove the deployment target.',
    )

  const retryDeployment = (
    id: string,
    deploymentId: string,
    expectedDeploymentRevision: number,
    expectedOperation: 'Apply' | 'Delete' = 'Apply',
  ) =>
    submit(
      () => retryWebServiceDeployment(apiBaseUrl, id, deploymentId, {
        expectedDeploymentRevision,
        expectedOperation,
      }, token, tenantId),
      'Unable to retry the deployment.',
    )

  const addHostname = async (
    webServiceId: string,
    deploymentId: string,
    input: { tenantDomainZoneId: string; relativeName: string },
  ) => {
    await submit(
      () => addWebServiceHostnameBinding(apiBaseUrl, webServiceId, deploymentId, input, token, tenantId),
      'Unable to add the hostname alias.',
    )
    await loadHostnameBindings(webServiceId, deploymentId)
  }

  const removeHostname = async (
    webServiceId: string,
    deploymentId: string,
    bindingId: string,
  ) => {
    await submit(
      () => deleteWebServiceHostnameBinding(apiBaseUrl, webServiceId, deploymentId, bindingId, token, tenantId),
      'Unable to remove the hostname alias.',
    )
    await loadHostnameBindings(webServiceId, deploymentId)
  }

  const remove = async (id: string) => {
    setDeletingId(id)
    setSubmitError('')
    try {
      await deleteWebService(apiBaseUrl, id, token, tenantId)
      removeItem(id)
    } catch (submitErrorValue) {
      setSubmitError(errorMessage(submitErrorValue, 'Unable to delete the web service.'))
      throw submitErrorValue
    } finally {
      setDeletingId(null)
    }
  }

  return {
    items,
    page,
    isCurrentPage,
    status,
    error,
    submitStatus,
    submitError,
    deletingId,
    availableTargets,
    targetsStatus,
    targetsError,
    computeProfilesByTarget,
    hostnameBindingsByDeployment,
    releasesByWebService,
    refresh,
    loadAvailableTargets,
    loadComputeProfiles,
    loadHostnameBindings,
    loadReleases,
    create,
    update,
    remove,
    addDeployment,
    updateDeployment,
    removeDeployment,
    retryDeployment,
    addHostname,
    removeHostname,
  }
}
