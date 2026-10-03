import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import { fetchResourceGroups } from '../../api'
import { ResourceGroupsPage } from '../../components/ResourceGroupsPage'
import { usePagedInventory } from '../../hooks/usePagedInventory'
import { useResourceGroupsManager } from '../../hooks/useResourceGroupsManager'
import { useAppServices } from '../../shell/AppContext'
import { catalogId, createId, positiveParam, resourceRouteParams, saveWorkspace, workspaceAtRoute } from '../../shell/workspaceState'
import { Button, ConfirmationDialog, EmptyState } from '../../ui/components'
import { TabPanel, Tabs } from '../../ui/Tabs'
import type { ResourceGroup } from '../../types'
import { GroupResourceView, NewGroupView } from './GroupResourceView'
import { readGroupRoute, readWorkspace, type GroupSection, type GroupWorkspace } from './workspaceState'

const workspaceId = 'resource-groups-workspace'
export function ResourceGroupsView() {
  const { api, auth, tenant, navigation, invalidateResources } = useAppServices()
  const route = readGroupRoute(navigation.search)
  const storageKey = `pole.resource-groups.workspace.v1.${auth.userId}.${api.tenantId}`
  const [routeSearch, setRouteSearch] = useState(navigation.search)
  const [creating, setCreating] = useState(route.resourceId === createId)
  const applyPage = (value: GroupWorkspace) => {
    const params = new URLSearchParams(navigation.search)
    return { ...value, tabs: value.tabs.map((tab) => tab.id === value.activeId ? { ...tab,
      resourcePage: positiveParam(params, 'resourcePage', 1, 1_000_000),
      resourcePageSize: positiveParam(params, 'resourcePageSize', 20, 100),
    } : tab) }
  }
  const [workspace, setWorkspace] = useState(() => {
    const initial = workspaceAtRoute(readWorkspace(storageKey), route)
    return route.resourceId === null ? initial : applyPage(initial)
  })
  if (routeSearch !== navigation.search) {
    setRouteSearch(navigation.search)
    setWorkspace((current) => applyPage(workspaceAtRoute(current, route)))
    if (route.resourceId === createId) setCreating(true)
  }
  const [closingId, setClosingId] = useState<string | null>(null)
  const [dirtyTabs, setDirtyTabs] = useState<Record<string, boolean>>({})
  const [names, setNames] = useState<Record<string, string>>({})
  const [refreshVersion, setRefreshVersion] = useState(0)
  const inventory = usePagedInventory({ scopeKey: `${api.baseUrl}:${api.tenantId}:${api.token}`, queryKey: JSON.stringify([route.pageNumber, route.pageSize, route.search]), enabled: Boolean(tenant.active),
    load: (signal) => fetchResourceGroups(api.baseUrl, api.token, api.tenantId, signal, route), getId: (group) => group.id })
  const manager = useResourceGroupsManager({ apiBaseUrl: api.baseUrl, token: api.token, tenantId: api.tenantId, enabled: true, loadInventory: false })
  const current = useRef({ workspace, navigation })
  const mounted = useRef(false)
  useLayoutEffect(() => { current.current = { workspace, navigation } }, [workspace, navigation])
  useLayoutEffect(() => { mounted.current = true; return () => { mounted.current = false } }, [])
  const navigateWorkspace = useCallback((next: GroupWorkspace, replace = false) => {
    if (!mounted.current) return
    setWorkspace(next)
    const navigation = current.current.navigation
    current.current = { workspace: next, navigation }
    navigation.setSearch(resourceRouteParams(navigation.search, next), { replace })
  }, [])
  const onLoaded = useCallback((group: ResourceGroup) => setNames((current) => current[group.id] === group.name ? current : { ...current, [group.id]: group.name }), [])
  const onDirtyChange = useCallback((id: string, dirty: boolean) => setDirtyTabs((current) => current[id] === dirty ? current : { ...current, [id]: dirty }), [])
  const onSection = (id: string, section: GroupSection) => navigateWorkspace({ ...workspace, tabs: workspace.tabs.map((tab) => tab.id === id ? { ...tab, section } : tab) })
  const onResourcePage = useCallback((id: string, resourcePage: number, resourcePageSize: number, replace = false) => {
    const workspace = current.current.workspace
    navigateWorkspace({ ...workspace, tabs: workspace.tabs.map((tab) => tab.id === id ? { ...tab, resourcePage, resourcePageSize } : tab) }, replace)
  }, [navigateWorkspace])
  useEffect(() => { saveWorkspace(storageKey, workspace) }, [storageKey, workspace])
  useEffect(() => {
    if (route.resourceId === null) navigation.setSearch(resourceRouteParams(navigation.search, workspace), { replace: true })
  }, [navigation, route.resourceId, workspace])
  const lastPage = Math.max(1, Math.ceil((inventory.page?.totalCount ?? 0) / route.pageSize))
  useEffect(() => {
    if (inventory.status === 'success' && inventory.isCurrentPage && route.pageNumber > lastPage) {
      const params = new URLSearchParams(navigation.search); params.set('page', String(lastPage)); navigation.setSearch(params, { replace: true })
    }
  }, [inventory.status, inventory.isCurrentPage, navigation, route.pageNumber, lastPage])
  const open = (id: string, edit = false) => {
    const workspace = current.current.workspace
    navigateWorkspace({ activeId: id, tabs: workspace.tabs.some((tab) => tab.id === id) ? workspace.tabs.map((tab) =>
      tab.id === id && edit ? { ...tab, section: 'configuration' } : tab) : [...workspace.tabs, { id, section: edit ? 'configuration' : 'resources' }] })
  }
  const close = (id: string) => {
    if (!mounted.current) return
    if (id === createId) setCreating(false)
    const workspace = current.current.workspace
    navigateWorkspace({ tabs: workspace.tabs.filter((tab) => tab.id !== id), activeId: workspace.activeId === id ? catalogId : workspace.activeId })
    setClosingId(null)
    requestAnimationFrame(() => document.querySelector<HTMLButtonElement>(`#${workspaceId} [role="tab"][aria-selected="true"]`)?.focus())
  }
  const label = (id: string) => id === createId ? 'New group' : names[id] ?? inventory.items.find((group) => group.id === id)?.name ?? `Group ${id.slice(0, 8)}`
  const refreshCatalog = () => { void inventory.refresh(); invalidateResources() }
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
        canManage={tenant.canManage} deletingId={manager.deletingId} deleteError={manager.submitError} onRefresh={() => { refreshCatalog(); setRefreshVersion((version) => version + 1) }}
        onCreate={() => { setCreating(true); navigateWorkspace({ ...workspace, activeId: createId }) }} onOpen={open}
        resourceHref={(id) => navigation.resourceHref({ resourceTypeId: 'pole/resource-group', resourceId: id, section: workspace.tabs.find((tab) => tab.id === id)?.section ?? 'resources' })}
        onDelete={async (id) => { await manager.remove(id); inventory.removeItem(id); close(id); refreshCatalog() }} />
    </TabPanel>
    {creating && <TabPanel id={workspaceId} tabId={createId} activeId={activeId}>{tenant.canManage ?
      <NewGroupView onDirtyChange={onDirtyChange} onCreated={(group, shouldOpen) => { refreshCatalog(); if (shouldOpen) { setCreating(false); onLoaded(group); open(group.id) } }} />
      : <EmptyState title="Tenant viewer access is read-only." />}</TabPanel>}
    {workspace.tabs.map((tab) => <TabPanel key={tab.id} id={workspaceId} tabId={tab.id} activeId={activeId}>
      <GroupResourceView id={tab.id} section={tab.section} active={activeId === tab.id} refreshVersion={refreshVersion}
        pageNumber={activeId === tab.id ? positiveParam(params, 'resourcePage', 1, 1_000_000) : tab.resourcePage ?? 1}
        pageSize={activeId === tab.id ? positiveParam(params, 'resourcePageSize', 20, 100) : tab.resourcePageSize ?? 20}
        onSection={onSection} onPage={onResourcePage} onLoaded={onLoaded} onDirtyChange={onDirtyChange} onChanged={refreshCatalog} />
    </TabPanel>)}
    {closingId && <ConfirmationDialog title={`Discard changes to ${label(closingId)}?`} confirmLabel="Discard and close" onCancel={() => setClosingId(null)} onConfirm={() => close(closingId)}>
      Your unsaved group metadata will be lost.</ConfirmationDialog>}
  </div>
}
