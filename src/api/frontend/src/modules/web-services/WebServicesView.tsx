import { useCallback, useEffect, useState } from 'react'
import { WebServicesPage } from '../../components/WebServicesPage'
import { useDomainZonesManager } from '../../hooks/useDomainZonesManager'
import { useResourceGroupsManager } from '../../hooks/useResourceGroupsManager'
import { useWebServicesManager } from '../../hooks/useWebServicesManager'
import { useConsoleServices } from '../../console/ConsoleContext'
import { ConfirmationDialog } from '../../console/components'
import { TabPanel, Tabs } from '../../console/Tabs'
import type { WebService } from '../../types'
import { NewWebServiceView } from './NewWebServiceView'
import { WebServiceResourceView } from './WebServiceResourceView'
import { catalogId, createId, readWorkspace, saveWorkspace, type ServiceSection } from './workspaceState'

const workspaceId = 'web-services-workspace'

export function WebServicesView() {
  const { api, auth, tenant } = useConsoleServices()
  const options = { apiBaseUrl: api.baseUrl, token: api.token, tenantId: api.tenantId, enabled: Boolean(tenant.active) }
  const storageKey = `pole.web-services.workspace.v1.${auth.userId}.${api.tenantId}`
  const [workspace, setWorkspace] = useState(() => readWorkspace(storageKey))
  const [creating, setCreating] = useState(false)
  const [closingId, setClosingId] = useState<string | null>(null)
  const [dirtyTabs, setDirtyTabs] = useState<Record<string, boolean>>({})
  const [names, setNames] = useState<Record<string, string>>({})
  const [refreshVersion, setRefreshVersion] = useState(0)
  const groups = useResourceGroupsManager(options)
  const services = useWebServicesManager(options)
  const zones = useDomainZonesManager(options)
  const onDirtyChange = useCallback((id: string, dirty: boolean) => {
    setDirtyTabs((current) => current[id] === dirty ? current : { ...current, [id]: dirty })
  }, [])
  const onLoaded = useCallback((service: WebService) => {
    setNames((current) => current[service.id] === service.name ? current : { ...current, [service.id]: service.name })
  }, [])
  const onSection = useCallback((id: string, section: ServiceSection) => {
    setWorkspace((current) => ({ ...current, tabs: current.tabs.map((tab) => tab.id === id ? { ...tab, section } : tab) }))
  }, [])
  useEffect(() => { saveWorkspace(storageKey, workspace) }, [storageKey, workspace])
  const open = (id: string, edit = false) => setWorkspace((current) => ({ activeId: id,
    tabs: current.tabs.some((tab) => tab.id === id) ? current.tabs.map((tab) =>
      tab.id === id && edit ? { ...tab, section: 'configuration' } : tab) : [...current.tabs, { id, section: edit ? 'configuration' : 'overview' }],
  }))
  const close = (id: string) => {
    if (id === createId) setCreating(false)
    setWorkspace((current) => ({ tabs: current.tabs.filter((tab) => tab.id !== id), activeId: current.activeId === id ? catalogId : current.activeId }))
    setClosingId(null)
    requestAnimationFrame(() => {
      document.querySelector<HTMLButtonElement>(`#${workspaceId} [role="tab"][aria-selected="true"]`)?.focus()
    })
  }
  const requestClose = (id: string) => dirtyTabs[id] ? setClosingId(id) : close(id)
  const label = (id: string) => id === createId ? 'New service' : names[id] ?? services.items.find((item) => item.id === id)?.name ?? `Service ${id.slice(0, 8)}`
  const refreshCatalog = () => { void services.refresh() }

  return <div className="web-service-workspace" id={workspaceId}>
    <Tabs id={workspaceId} label="Web service workspace" activeId={workspace.activeId}
      onSelect={(activeId) => setWorkspace((current) => ({ ...current, activeId }))} tabs={[
        { id: catalogId, label: 'All services' },
        ...(creating ? [{ id: createId, label: 'New service', dirty: dirtyTabs[createId], onClose: () => requestClose(createId) }] : []),
        ...workspace.tabs.map((tab) => ({ id: tab.id, label: label(tab.id), dirty: dirtyTabs[tab.id], onClose: () => requestClose(tab.id) })),
      ]} />
    <TabPanel id={workspaceId} tabId={catalogId} activeId={workspace.activeId}>
      <WebServicesPage items={services.items} resourceGroups={groups.items} status={services.status} error={services.error}
        deletingId={services.deletingId} deleteError={services.submitError} canManage={tenant.canManage}
        onRefresh={() => { refreshCatalog(); setRefreshVersion((current) => current + 1) }} onCreate={() => {
          setCreating(true)
          setWorkspace((current) => ({ ...current, activeId: createId }))
        }} onOpen={open} onDelete={async (id) => { await services.remove(id); close(id) }} />
    </TabPanel>
    {creating && <TabPanel id={workspaceId} tabId={createId} activeId={workspace.activeId}>
      <NewWebServiceView groups={groups.items} onDirtyChange={onDirtyChange} onCreated={(id, shouldOpen) => {
        refreshCatalog()
        if (shouldOpen) { setCreating(false); open(id) }
      }} />
    </TabPanel>}
    {workspace.tabs.map((tab) => <TabPanel key={tab.id} id={workspaceId} tabId={tab.id} activeId={workspace.activeId}>
      <WebServiceResourceView id={tab.id} section={tab.section} active={workspace.activeId === tab.id} refreshVersion={refreshVersion}
        groups={groups.items} zones={zones.items} onSection={onSection} onLoaded={onLoaded} onDirtyChange={onDirtyChange} onChanged={refreshCatalog} />
    </TabPanel>)}
    {closingId && <ConfirmationDialog title={`Discard changes to ${label(closingId)}?`} confirmLabel="Discard and close"
      onCancel={() => setClosingId(null)} onConfirm={() => close(closingId)}><p>Your unsaved changes will be lost.</p></ConfirmationDialog>}
  </div>
}
