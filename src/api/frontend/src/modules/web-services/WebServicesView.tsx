import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import { WebServicesPage } from '../../components/WebServicesPage'
import { useDomainZonesManager } from '../../hooks/useDomainZonesManager'
import { useResourceGroupsManager } from '../../hooks/useResourceGroupsManager'
import { useWebServicesManager } from '../../hooks/useWebServicesManager'
import { useAppServices } from '../../shell/AppContext'
import { Button, ConfirmationDialog, EmptyState } from '../../ui/components'
import { TabPanel, Tabs } from '../../ui/Tabs'
import type { WebService } from '../../types'
import { NewWebServiceView } from './NewWebServiceView'
import { WebServiceResourceView } from './WebServiceResourceView'
import { catalogId, createId, readServiceRoute, readWorkspace, saveWorkspace, serviceRouteParams, workspaceAtRoute, type ServiceSection, type WebServiceWorkspace } from './workspaceState'

const workspaceId = 'web-services-workspace'

export function WebServicesView() {
  const { api, auth, tenant, navigation, invalidateResources } = useAppServices()
  const route = readServiceRoute(navigation.search)
  const options = { apiBaseUrl: api.baseUrl, token: api.token, tenantId: api.tenantId, enabled: Boolean(tenant.active) }
  const storageKey = `pole.web-services.workspace.v1.${auth.userId}.${api.tenantId}`
  const [workspace, setWorkspace] = useState(() => workspaceAtRoute(readWorkspace(storageKey), route))
  const [creating, setCreating] = useState(route.resourceId === createId)
  const [routeSearch, setRouteSearch] = useState(navigation.search)
  if (routeSearch !== navigation.search) {
    setRouteSearch(navigation.search)
    setWorkspace((current) => workspaceAtRoute(current, route))
    if (route.resourceId === createId) setCreating(true)
  }
  const [closingId, setClosingId] = useState<string | null>(null)
  const [dirtyTabs, setDirtyTabs] = useState<Record<string, boolean>>({})
  const [names, setNames] = useState<Record<string, string>>({})
  const [refreshVersion, setRefreshVersion] = useState(0)
  const groups = useResourceGroupsManager(options)
  const services = useWebServicesManager({ ...options, pageNumber: route.pageNumber, pageSize: route.pageSize, search: route.search })
  const zones = useDomainZonesManager(options)
  const current = useRef({ workspace, navigation })
  const mounted = useRef(false)
  useLayoutEffect(() => { current.current = { workspace, navigation } }, [workspace, navigation])
  useLayoutEffect(() => { mounted.current = true; return () => { mounted.current = false } }, [])
  const onDirtyChange = useCallback((id: string, dirty: boolean) => {
    setDirtyTabs((current) => current[id] === dirty ? current : { ...current, [id]: dirty })
  }, [])
  const onLoaded = useCallback((service: WebService) => {
    setNames((current) => current[service.id] === service.name ? current : { ...current, [service.id]: service.name })
  }, [])
  const navigateWorkspace = (next: WebServiceWorkspace, replace = false) => {
    if (!mounted.current) return
    setWorkspace(next)
    const navigation = current.current.navigation
    current.current = { workspace: next, navigation }
    navigation.setSearch(serviceRouteParams(navigation.search, next), { replace })
  }
  const onSection = (id: string, section: ServiceSection) => navigateWorkspace({ ...workspace,
    tabs: workspace.tabs.map((tab) => tab.id === id ? { ...tab, section } : tab),
  })
  useEffect(() => { saveWorkspace(storageKey, workspace) }, [storageKey, workspace])
  useEffect(() => {
    if (route.resourceId === null) navigation.setSearch(serviceRouteParams(navigation.search, workspace), { replace: true })
  }, [navigation, route.resourceId, workspace])
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
  const open = (id: string, edit = false, section?: ServiceSection) => {
    const workspace = current.current.workspace
    navigateWorkspace({ activeId: id,
      tabs: workspace.tabs.some((tab) => tab.id === id) ? workspace.tabs.map((tab) =>
        tab.id === id && (edit || section) ? { ...tab, section: section ?? 'configuration' } : tab) : [...workspace.tabs, { id, section: section ?? (edit ? 'configuration' : 'overview') }],
    })
  }
  const close = (id: string) => {
    if (!mounted.current) return
    const workspace = current.current.workspace
    if (id === createId) setCreating(false)
    navigateWorkspace({ tabs: workspace.tabs.filter((tab) => tab.id !== id), activeId: workspace.activeId === id ? catalogId : workspace.activeId })
    setClosingId(null)
    requestAnimationFrame(() => {
      document.querySelector<HTMLButtonElement>(`#${workspaceId} [role="tab"][aria-selected="true"]`)?.focus()
    })
  }
  const requestClose = (id: string) => dirtyTabs[id] ? setClosingId(id) : close(id)
  const label = (id: string) => id === createId ? 'New service' : names[id] ?? services.items.find((item) => item.id === id)?.name ?? `Service ${id.slice(0, 8)}`
  const refreshCatalog = () => { void services.refresh(); invalidateResources() }

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
        onRefresh={() => { refreshCatalog(); setRefreshVersion((current) => current + 1) }} onCreate={() => {
          setCreating(true)
          navigateWorkspace({ ...workspace, activeId: createId })
        }} onOpen={open} onDelete={async (id) => { await services.remove(id); close(id); invalidateResources() }} />
    </TabPanel>
    {creating && <TabPanel id={workspaceId} tabId={createId} activeId={activeId}>
      {tenant.canManage ? <NewWebServiceView groups={groups.items} groupsStatus={groups.status} groupsError={groups.error} onRefreshGroups={groups.refresh}
        onDirtyChange={onDirtyChange} onCreated={(id, shouldOpen, section) => {
        refreshCatalog()
        if (shouldOpen) { setCreating(false); open(id, false, section) }
      }} /> : <EmptyState title="Tenant viewer access is read-only." />}
    </TabPanel>}
    {workspace.tabs.map((tab) => <TabPanel key={tab.id} id={workspaceId} tabId={tab.id} activeId={activeId}>
      <WebServiceResourceView id={tab.id} section={tab.section} active={activeId === tab.id} refreshVersion={refreshVersion}
        groups={groups.items} zones={zones.items} onSection={onSection} onLoaded={onLoaded} onDirtyChange={onDirtyChange} onChanged={refreshCatalog} />
    </TabPanel>)}
    {closingId && <ConfirmationDialog title={`Discard changes to ${label(closingId)}?`} confirmLabel="Discard and close"
      onCancel={() => setClosingId(null)} onConfirm={() => close(closingId)}><p>Your unsaved changes will be lost.</p></ConfirmationDialog>}
  </div>
}
