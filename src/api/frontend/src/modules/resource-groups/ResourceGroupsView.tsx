import { useWorkspaceController } from '../../shell/useWorkspaceController'
import { useCallback, useEffect } from 'react'
import { useProviderInvalidation } from '../../data/useProviderInvalidation'
import { ResourceGroupsPage } from '../../components/ResourceGroupsPage'
import { useResourceGroupInventory } from './useResourceGroups'
import { useResourceGroupsManager } from './useResourceGroups'
import { useAppServices } from '../../shell/AppContext'
import { catalogId, createId, positiveParam } from '../../shell/workspaceState'
import { Button, ConfirmationDialog, EmptyState } from '../../ui/components'
import { TabPanel, Tabs } from '../../ui/Tabs'
import type { ResourceGroup } from '../../types'
import { GroupResourceView, NewGroupView } from './GroupResourceView'
import { groupSections, type GroupWorkspace } from './workspaceState'

const workspaceId = 'resource-groups-workspace'
export function ResourceGroupsView() {
  const { api, tenant, navigation } = useAppServices()
  const { route, workspace, creating, setCreating, closingId, setClosingId, names, dirtyTabs,
    updateTab, navigateWorkspace, loaded, dirty: onDirtyChange, open: openTab, section: onSection, close } =
    useWorkspaceController({ id: 'resource-groups', sections: groupSections, defaultSection: 'resources', decorateRoute: (workspace: GroupWorkspace, search) => {
      const params = new URLSearchParams(search)
      return { ...workspace, tabs: workspace.tabs.map((tab) => tab.id === workspace.activeId ? { ...tab,
        resourcePage: positiveParam(params, 'resourcePage', 1, 1_000_000),
        resourcePageSize: positiveParam(params, 'resourcePageSize', 20, 100),
      } : tab) }
    } })
  const origin = useProviderInvalidation(api)
  const inventory = useResourceGroupInventory(api, route.pageNumber, route.pageSize, route.search, Boolean(tenant.active), route.valid && workspace.activeId === catalogId)
  const manager = useResourceGroupsManager({ apiBaseUrl: api.baseUrl, token: api.token, tenantId: api.tenantId, enabled: tenant.canManage, loadInventory: false })
  const onLoaded = useCallback((group: ResourceGroup) => loaded(group.id, group.name), [loaded])
  const onResourcePage = useCallback((id: string, resourcePage: number, resourcePageSize: number, replace = false) => {
    updateTab(id, { resourcePage, resourcePageSize }, replace)
  }, [updateTab])
  const lastPage = Math.max(1, Math.ceil((inventory.page?.totalCount ?? 0) / route.pageSize))
  useEffect(() => {
    if (inventory.status === 'success' && inventory.isCurrentPage && route.pageNumber > lastPage) {
      const params = new URLSearchParams(navigation.search); params.set('page', String(lastPage)); navigation.setSearch(params, { replace: true })
    }
  }, [inventory.status, inventory.isCurrentPage, navigation, route.pageNumber, lastPage])
  const open = (id: string, edit = false) => openTab(id, edit ? 'configuration' : undefined)
  const label = (id: string) => id === createId ? 'New group' : names[id] ?? inventory.items.find((group) => group.id === id)?.name ?? `Group ${id.slice(0, 8)}`
  const refreshCatalog = () => { void inventory.refresh(); }
  const setInventory = (values: { pageNumber?: number; pageSize?: number; search?: string }) => {
    const params = new URLSearchParams(navigation.search)
    params.set('resource', catalogId); params.delete('section'); params.delete('resourcePage'); params.delete('resourcePageSize')
    params.set('page', String(values.pageNumber ?? route.pageNumber)); params.set('pageSize', String(values.pageSize ?? route.pageSize))
    const search = values.search ?? route.search
    if (search) params.set('search', search); else params.delete('search')
    navigation.setSearch(params)
  }
  const activeId = route.valid ? workspace.activeId : 'invalid-link'
  const params = new URLSearchParams(navigation.search)
  return <div className="resource-group-workspace" id={workspaceId}>
    {!route.valid && <EmptyState title="This group link is invalid." error><Button onClick={() => navigateWorkspace({ ...workspace, activeId: catalogId }, true)}>Open group catalog</Button></EmptyState>}
    <Tabs id={workspaceId} label="Resource group workspace" activeId={activeId} onSelect={(activeId) => navigateWorkspace({ ...workspace, activeId })} tabs={[
      { id: catalogId, label: 'All groups' }, ...(creating ? [{ id: createId, label: 'New group', dirty: dirtyTabs[createId], onClose: () => dirtyTabs[createId] ? setClosingId(createId) : close(createId) }] : []),
      ...workspace.tabs.map((tab) => ({ id: tab.id, label: label(tab.id), dirty: dirtyTabs[tab.id], onClose: () => dirtyTabs[tab.id] ? setClosingId(tab.id) : close(tab.id) })),
    ]} />
    <TabPanel id={workspaceId} tabId={catalogId} activeId={activeId}>
      <ResourceGroupsPage items={inventory.items} page={inventory.page} status={inventory.status} error={inventory.error} pageNumber={route.pageNumber} pageSize={route.pageSize} search={route.search}
        onPage={(pageNumber) => setInventory({ pageNumber })} onPageSize={(pageSize) => setInventory({ pageSize, pageNumber: 1 })} onSearch={(search) => setInventory({ search, pageNumber: 1 })}
        canManage={tenant.canManage} deletingId={manager.deletingId} deleteError={manager.submitError} onRefresh={() => { void origin.invalidate([['resource-groups']]) }}
        onCreate={() => { setCreating(true); navigateWorkspace({ ...workspace, activeId: createId }) }} onOpen={open}
        resourceHref={(id) => navigation.resourceHref({ resourceTypeId: 'pole/resource-group', resourceId: id, section: workspace.tabs.find((tab) => tab.id === id)?.section ?? 'resources' })}
        onDelete={async (id) => { const removed = await manager.remove(id); if (removed && origin.isCurrent()) { close(id); refreshCatalog() } }} />
    </TabPanel>
    {creating && <TabPanel id={workspaceId} tabId={createId} activeId={activeId}>{tenant.canManage ?
      <NewGroupView onDirtyChange={onDirtyChange} onCreated={(group, shouldOpen) => { refreshCatalog(); if (shouldOpen) { setCreating(false); onLoaded(group); open(group.id) } }} />
      : <EmptyState title="Tenant viewer access is read-only." />}</TabPanel>}
    {workspace.tabs.map((tab) => <TabPanel key={tab.id} id={workspaceId} tabId={tab.id} activeId={activeId}>
      <GroupResourceView id={tab.id} section={tab.section} active={activeId === tab.id}
        pageNumber={activeId === tab.id ? positiveParam(params, 'resourcePage', 1, 1_000_000) : tab.resourcePage ?? 1}
        pageSize={activeId === tab.id ? positiveParam(params, 'resourcePageSize', 20, 100) : tab.resourcePageSize ?? 20}
        onSection={onSection} onPage={onResourcePage} onLoaded={onLoaded} onDirtyChange={onDirtyChange} onChanged={refreshCatalog} />
    </TabPanel>)}
    {closingId && <ConfirmationDialog title={`Discard changes to ${label(closingId)}?`} confirmLabel="Discard and close" onCancel={() => setClosingId(null)} onConfirm={() => close(closingId)}>
      Your unsaved group metadata will be lost.</ConfirmationDialog>}
  </div>
}
