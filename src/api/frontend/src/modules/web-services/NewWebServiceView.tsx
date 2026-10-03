import { useEffect, useRef, useState } from 'react'
import { WebServiceForm } from '../../components/WebServiceForm'
import { useConsoleServices } from '../../console/ConsoleContext'
import { useUnsavedChanges } from '../../console/useUnsavedChanges'
import { useWebServicesManager } from '../../hooks/useWebServicesManager'
import type { ResourceGroup } from '../../types'
import { createId } from './workspaceState'

export function NewWebServiceView({ groups, onCreated, onDirtyChange }:
  { groups: ResourceGroup[]; onCreated: (id: string, open: boolean) => void; onDirtyChange: (id: string, dirty: boolean) => void }) {
  const { api, notifications } = useConsoleServices()
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
    availableTargets={manager.availableTargets} targetsStatus={manager.targetsStatus} targetsError={manager.targetsError}
    computeProfilesByTarget={manager.computeProfilesByTarget} onLoadTargets={manager.loadAvailableTargets}
    onLoadComputeProfiles={manager.loadComputeProfiles} submitStatus={manager.submitStatus} submitError={manager.submitError}
    onDirtyChange={setDirty} onSubmit={async (input) => {
      const created = await manager.create(input)
      notifications.publish('Service configuration accepted. Each deployment reconciles independently.')
      onCreated(created.webServiceId, mounted.current)
    }} /></div>
}
