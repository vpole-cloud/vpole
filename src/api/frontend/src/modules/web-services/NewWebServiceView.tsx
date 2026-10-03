import { useCallback, useEffect, useRef, useState } from 'react'
import { fetchIsolationScopes } from '../../api'
import { WebServiceForm } from '../../components/WebServiceForm'
import { useAppServices } from '../../shell/AppContext'
import { useUnsavedChanges } from '../../shell/useUnsavedChanges'
import { useWebServicesManager } from '../../hooks/useWebServicesManager'
import type { ResourceGroup } from '../../types'
import { createId } from './workspaceState'

export function NewWebServiceView({ groups, onCreated, onDirtyChange }:
  { groups: ResourceGroup[]; onCreated: (id: string, open: boolean) => void; onDirtyChange: (id: string, dirty: boolean) => void }) {
  const { api, notifications } = useAppServices()
  const loadIsolationScopes = useCallback((groupId: string, signal?: AbortSignal) =>
    fetchIsolationScopes(api.baseUrl, groupId, api.token, api.tenantId, signal), [api.baseUrl, api.token, api.tenantId])
  const manager = useWebServicesManager({ apiBaseUrl: api.baseUrl, token: api.token, tenantId: api.tenantId,
    enabled: false, refreshAfterMutation: false })
  const [dirty, setDirty] = useState(false)
  const mounted = useRef(false)
  useEffect(() => { mounted.current = true; return () => { mounted.current = false } }, [])
  useUnsavedChanges(dirty, 'New service')
  useEffect(() => {
    onDirtyChange(createId, dirty)
    return () => onDirtyChange(createId, false)
  }, [dirty, onDirtyChange])
  return <div className="web-service-editor"><WebServiceForm mode="create" resourceGroups={groups}
    onLoadIsolationScopes={loadIsolationScopes}
    availableTargets={manager.availableTargets} targetsStatus={manager.targetsStatus} targetsError={manager.targetsError}
    computeProfilesByTarget={manager.computeProfilesByTarget} onLoadTargets={manager.loadAvailableTargets}
    onLoadComputeProfiles={manager.loadComputeProfiles} submitStatus={manager.submitStatus} submitError={manager.submitError}
    onDirtyChange={setDirty} onSubmit={async (input) => {
      const created = await manager.create(input)
      notifications.publish('Service configuration accepted. Each deployment reconciles independently.')
      onCreated(created.webServiceId, mounted.current)
    }} /></div>
}
