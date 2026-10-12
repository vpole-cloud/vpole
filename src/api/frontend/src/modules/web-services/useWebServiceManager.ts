import { fetchScopedQuery } from '../../data/fetchScopedQuery'
import { useWebServiceEligibility } from './useWebServiceEligibility'
import { useAppServices } from '../../shell/AppContext'
import { useScopedMutation } from '../../hooks/useScopedMutation'
import { invalidateWebServiceState } from './invalidate'
import { useApiScope } from '../../data/useApiScope'
import { useCallback, useState } from 'react'
import {
  addWebServiceDeployment,
  addWebServiceHostnameBinding,
  createWebService,
  deleteWebService,
  deleteWebServiceDeployment,
  deleteWebServiceHostnameBinding,
  fetchWebServiceHostnameBindings,
  retryWebServiceDeployment,
  updateWebService,
  updateWebServiceDeployment,
} from '../../api'
import type {
  HostnameBinding,
  LoadStatus,
  WebServiceDeploymentInput,
  WebServiceDeploymentUpdateInput,
  WebServiceInput,
} from '../../types'
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
  active?: boolean
  resourceGroupId?: string
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
  active = true,
  resourceGroupId,
}: UseWebServicesManagerOptions) {
  const { tenant } = useAppServices()
  const scope = useApiScope({ baseUrl: apiBaseUrl, token, tenantId })
  const { credential, isCurrent } = scope
  const mutation = useScopedMutation(`${scope.identity}:web-service-write:${tenant.canManage}`)
  const { items, page, isCurrentPage, status, error, refresh, removeItem } = useWebServiceInventory({ apiBaseUrl, token, tenantId, enabled, pageNumber, pageSize, search })
  const [submitStatus, setSubmitStatus] = useState<LoadStatus>('idle')
  const [submitError, setSubmitError] = useState('')
  const [deletingId, setDeletingId] = useState<string | null>(null)
  const eligibility = useWebServiceEligibility({ baseUrl: apiBaseUrl, token, tenantId }, active && tenant.canManage, resourceGroupId)
  const [hostnameBindingsByDeployment, setHostnameBindingsByDeployment] = useState<
    Record<string, HostnameBinding[]>
  >({})

  const loadHostnameBindings = useCallback(async (webServiceId: string, deploymentId: string) => {
    try {
      const bindings = await fetchScopedQuery(scope, ['web-services', 'hostnames', webServiceId, deploymentId], (signal) => fetchWebServiceHostnameBindings(
        apiBaseUrl,
        webServiceId,
        deploymentId,
        credential(),
        tenantId,
        signal,
      ))
      if (!isCurrent()) return []
      setHostnameBindingsByDeployment((current) => ({ ...current, [deploymentId]: bindings }))
      return bindings
    } catch (loadError) {
      if (!isCurrent()) return []
      setSubmitError(errorMessage(loadError, 'Unable to load hostname bindings.'))
      return []
    }
  }, [apiBaseUrl, tenantId, credential, scope, isCurrent])

  const submit = async <T,>(action: () => Promise<T>, fallback: string) => {
    if (!tenant.canManage) throw new Error('Tenant viewer access is read-only.')
    try {
      const accepted = await mutation.run(async () => {
        setSubmitStatus('loading'); setSubmitError('')
        return { value: await action() }
      }, async () => {
        setSubmitStatus('success')
        await invalidateWebServiceState(scope)
        if (refreshAfterMutation && isCurrent()) await refresh()
      })
      if (!accepted || !isCurrent()) throw new DOMException('Aborted', 'AbortError')
      return accepted.value
    } catch (failure) {
      if (!isCurrent()) throw new DOMException('Aborted', 'AbortError')
      if (!(failure instanceof DOMException && failure.name === 'AbortError')) {
        setSubmitStatus('error'); setSubmitError(errorMessage(failure, fallback))
      }
      throw failure
    }
  }

  const create = async (input: WebServiceInput) => {
    return submit(
      () => createWebService(apiBaseUrl, input, credential(), tenantId),
      'Unable to create the web service.',
    )
  }

  const update = async (id: string, input: WebServiceInput) => {
    return submit(
      () => updateWebService(apiBaseUrl, id, input, credential(), tenantId),
      'Unable to update the web service.',
    )
  }

  const addDeployment = (id: string, input: WebServiceDeploymentInput) =>
    submit(
      () => addWebServiceDeployment(apiBaseUrl, id, input, credential(), tenantId),
      'Unable to add the deployment target.',
    )

  const updateDeployment = (
    id: string,
    deploymentId: string,
    input: WebServiceDeploymentUpdateInput,
  ) =>
    submit(
      () => updateWebServiceDeployment(apiBaseUrl, id, deploymentId, input, credential(), tenantId),
      'Unable to update the deployment target.',
    )

  const removeDeployment = (id: string, deploymentId: string) =>
    submit(
      () => deleteWebServiceDeployment(apiBaseUrl, id, deploymentId, credential(), tenantId),
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
      }, credential(), tenantId),
      'Unable to retry the deployment.',
    )

  const addHostname = async (
    webServiceId: string,
    deploymentId: string,
    input: { tenantDomainZoneId: string; relativeName: string },
  ) => {
    await submit(
      () => addWebServiceHostnameBinding(apiBaseUrl, webServiceId, deploymentId, input, credential(), tenantId),
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
      () => deleteWebServiceHostnameBinding(apiBaseUrl, webServiceId, deploymentId, bindingId, credential(), tenantId),
      'Unable to remove the hostname alias.',
    )
    await loadHostnameBindings(webServiceId, deploymentId)
  }

  const remove = async (id: string) => {
    setDeletingId(id)
    setSubmitError('')
    try {
      await submit(() => deleteWebService(apiBaseUrl, id, credential(), tenantId), 'Unable to delete the web service.')
      if (!isCurrent()) throw new DOMException('Aborted', 'AbortError')
      removeItem(id)
    } catch (submitErrorValue) {
      if (!isCurrent()) throw new DOMException('Aborted', 'AbortError')
      setSubmitError(errorMessage(submitErrorValue, 'Unable to delete the web service.'))
      throw submitErrorValue
    } finally {
      if (isCurrent()) setDeletingId(null)
    }
  }

  return {
    items,
    page,
    isCurrentPage,
    status,
    error,
    submitStatus: mutation.busy ? 'loading' as const : submitStatus === 'loading' ? 'idle' as const : submitStatus,
    submitError,
    deletingId,
    ...eligibility,
    hostnameBindingsByDeployment,
    refresh,
    loadHostnameBindings,
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
