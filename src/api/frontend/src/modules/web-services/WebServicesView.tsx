import { useProviderInvalidation } from '../../data/useProviderInvalidation'
import { useWorkspaceController } from '../../shell/useWorkspaceController'
import { useCallback, useEffect } from 'react'
import { WebServicesPage } from '../../components/WebServicesPage'
import { useResourceGroupChoices } from '../../hooks/useResourceGroupChoices'
import { useWebServicesManager } from './useWebServiceManager'
import { useAppServices } from '../../shell/AppContext'
import { Button, ConfirmationDialog, EmptyState } from '../../ui/components'
import { TabPanel, Tabs } from '../../ui/Tabs'
import type { WebService } from '../../types'
import { NewWebServiceView } from './NewWebServiceView'
import { WebServiceResourceView } from './WebServiceResourceView'
import { catalogId, createId, serviceSections, type ServiceSection } from './workspaceState'

const workspaceId = 'web-services-workspace'

export function WebServicesView() {
  const { api, tenant, navigation } = useAppServices()
  const { route, workspace, creating, setCreating, closingId, setClosingId, names, dirtyTabs,
    navigateWorkspace, loaded, dirty: onDirtyChange, open: openTab, section: onSection, close, requestClose } =
    useWorkspaceController({ id: 'web-services', sections: serviceSections, defaultSection: 'overview' })
  const origin = useProviderInvalidation(api)
  const options = { apiBaseUrl: api.baseUrl, token: api.token, tenantId: api.tenantId, enabled: Boolean(tenant.active) }
  const groups = useResourceGroupChoices(options)
  const services = useWebServicesManager({ ...options, pageNumber: route.pageNumber, pageSize: route.pageSize, search: route.search })
  const onLoaded = useCallback((service: WebService) => loaded(service.id, service.name), [loaded])
  const setInventory = (values: { pageNumber?: number; pageSize?: number; search?: string }, replace = false) => {
    const params = new URLSearchParams(navigation.search)
    params.set('resource', catalogId)
    params.delete('section')
    params.set('page', String(values.pageNumber ?? route.pageNumber))
    params.set('pageSize', String(values.pageSize ?? route.pageSize))
    const search = values.search ?? route.search
    if (search) params.set('search', search)
    else params.delete('search')
    navigation.setSearch(params, { replace })
  }
  // Deletion or an old bookmark can leave the requested page beyond the inventory.
  const lastPage = services.page ? Math.max(1, Math.ceil(services.page.totalCount / route.pageSize)) : route.pageNumber
  useEffect(() => {
    if (services.status === 'success' && services.isCurrentPage && route.pageNumber > lastPage) {
      const params = new URLSearchParams(navigation.search)
      params.set('page', String(lastPage))
      navigation.setSearch(params, { replace: true })
    }
  }, [navigation, route.pageNumber, lastPage, services.status, services.isCurrentPage])
  const open = (id: string, edit = false, section?: ServiceSection) => openTab(id, section ?? (edit ? 'configuration' : undefined))
  const label = (id: string) => id === createId ? 'New service' : names[id] ?? services.items.find((item) => item.id === id)?.name ?? `Service ${id.slice(0, 8)}`
  const refreshCatalog = () => { void services.refresh(); }

  const activeId = route.valid ? workspace.activeId : 'invalid-link'
  return <div className="web-service-workspace" id={workspaceId}>
    {!route.valid && <EmptyState title="This service link is invalid." error>
      <Button onClick={() => navigateWorkspace({ ...workspace, activeId: catalogId }, true)}>Open service catalog</Button>
    </EmptyState>}
    <Tabs id={workspaceId} label="Web service workspace" activeId={activeId}
      onSelect={(activeId) => navigateWorkspace({ ...workspace, activeId })} tabs={[
        { id: catalogId, label: 'All services' },
        ...(creating ? [{ id: createId, label: 'New service', dirty: dirtyTabs[createId], onClose: () => requestClose(createId) }] : []),
        ...workspace.tabs.map((tab) => ({ id: tab.id, label: label(tab.id), dirty: dirtyTabs[tab.id], onClose: () => requestClose(tab.id) })),
      ]} />
    <TabPanel id={workspaceId} tabId={catalogId} activeId={activeId}>
      <WebServicesPage items={services.items} resourceGroups={groups.items} status={services.status} error={services.error}
        resourceHref={(id) => navigation.resourceHref({ resourceTypeId: 'pole/web-service', resourceId: id, section: workspace.tabs.find((tab) => tab.id === id)?.section ?? 'overview' })}
        page={services.page} pageNumber={route.pageNumber} pageSize={route.pageSize} search={route.search}
        onPage={(pageNumber) => setInventory({ pageNumber })} onPageSize={(pageSize) => setInventory({ pageSize, pageNumber: 1 })}
        onSearch={(search) => setInventory({ search, pageNumber: 1 })}
        deletingId={services.deletingId} deleteError={services.submitError} canManage={tenant.canManage}
        onRefresh={() => { void origin.invalidate([['web-services']]) }} onCreate={() => {
          setCreating(true)
          navigateWorkspace({ ...workspace, activeId: createId })
        }} onOpen={open} onDelete={async (id) => { await services.remove(id); if (origin.isCurrent()) close(id); }} />
    </TabPanel>
    {creating && <TabPanel id={workspaceId} tabId={createId} activeId={activeId}>
      {tenant.canManage ? <NewWebServiceView active={activeId === createId} groups={groups.items} groupsStatus={groups.status} groupsError={groups.error} onRefreshGroups={groups.refresh}
        onDirtyChange={onDirtyChange} onCreated={(id, shouldOpen, section) => {
        refreshCatalog()
        if (shouldOpen) { setCreating(false); open(id, false, section) }
      }} /> : <EmptyState title="Tenant viewer access is read-only." />}
    </TabPanel>}
    {workspace.tabs.map((tab) => <TabPanel key={tab.id} id={workspaceId} tabId={tab.id} activeId={activeId}>
      <WebServiceResourceView id={tab.id} section={tab.section} active={activeId === tab.id}
        groups={groups.items} onSection={onSection} onLoaded={onLoaded} onDirtyChange={onDirtyChange} onChanged={refreshCatalog} />
    </TabPanel>)}
    {closingId && <ConfirmationDialog title={`Discard changes to ${label(closingId)}?`} confirmLabel="Discard and close"
      onCancel={() => setClosingId(null)} onConfirm={() => close(closingId)}><p>Your unsaved changes will be lost.</p></ConfirmationDialog>}
  </div>
}
