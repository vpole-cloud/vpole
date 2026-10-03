import { useCallback, useEffect, useLayoutEffect, useRef, useState, type ComponentType } from 'react'
import { useAppServices } from '../shell/AppContext'
import { catalogId, createId, readResourceRoute, readWorkspace, resourceRouteParams, saveWorkspace, workspaceAtRoute, type ResourceWorkspace as Workspace } from '../shell/workspaceState'
import { Button, ConfirmationDialog, EmptyState } from './components'
import { Tabs, TabPanel } from './Tabs'

export interface WorkspaceActions<S extends string> {
  open: (id: string, section?: S) => void
  created: (id: string, name: string) => void
  loaded: (id: string, name: string) => void
  dirty: (id: string, dirty: boolean) => void
  changed: () => void
  closed: (id: string) => void
}

// Only workspace navigation mechanics are shared. Providers own their data, forms and actions.
export function ResourceWorkspace<S extends string>({ id, label, newLabel, sections, defaultSection, Catalog, Create, Resource }:
  { id: string; label: string; newLabel: string; sections: readonly S[]; defaultSection: S;
    Catalog: ComponentType<{ actions: WorkspaceActions<S>; version: number }>;
    Create: ComponentType<{ actions: WorkspaceActions<S>; active: boolean }>;
    Resource: ComponentType<{ id: string; section: S; active: boolean; version: number; onSection: (section: S) => void; actions: WorkspaceActions<S> }> }) {
  const { auth, api, tenant, navigation, invalidateResources } = useAppServices()
  const storageKey = `pole.${id}.workspace.v1.${auth.userId}.${api.tenantId}`
  const route = readResourceRoute(navigation.search, sections, defaultSection)
  const [workspace, setWorkspace] = useState(() => workspaceAtRoute(readWorkspace(storageKey, sections, defaultSection), route))
  const [routeSearch, setRouteSearch] = useState(navigation.search)
  const [creating, setCreating] = useState(route.resourceId === createId)
  if (routeSearch !== navigation.search) {
    setRouteSearch(navigation.search)
    setWorkspace((current) => workspaceAtRoute(current, route))
    if (route.resourceId === createId) setCreating(true)
  }
  const [names, setNames] = useState<Record<string, string>>({})
  const [dirtyTabs, setDirtyTabs] = useState<Record<string, boolean>>({})
  const [closing, setClosing] = useState<string | null>(null)
  const [version, setVersion] = useState(0)
  const current = useRef({ workspace, navigation })
  const mounted = useRef(false)
  useLayoutEffect(() => { current.current = { workspace, navigation } }, [workspace, navigation])
  useLayoutEffect(() => { mounted.current = true; return () => { mounted.current = false } }, [])
  const navigate = useCallback((next: Workspace<S>, replace = false) => {
    if (!mounted.current) return
    const navigation = current.current.navigation
    current.current = { workspace: next, navigation }
    setWorkspace(next)
    navigation.setSearch(resourceRouteParams(navigation.search, next), { replace })
  }, [])
  const loaded = useCallback((id: string, name: string) => setNames((current) => current[id] === name ? current : { ...current, [id]: name }), [])
  const dirty = useCallback((id: string, value: boolean) => setDirtyTabs((current) => current[id] === value ? current : { ...current, [id]: value }), [])
  const open = useCallback((id: string, section?: S) => {
    const workspace = current.current.workspace
    if (id === createId || id === catalogId) { navigate({ ...workspace, activeId: id }); return }
    navigate({ activeId: id, tabs: workspace.tabs.some((tab) => tab.id === id)
      ? workspace.tabs.map((tab) => tab.id === id && section ? { ...tab, section } : tab)
      : [...workspace.tabs, { id, section: section ?? defaultSection }] })
  }, [defaultSection, navigate])
  const closed = (id: string) => {
    if (!mounted.current) return
    if (id === createId) setCreating(false)
    const workspace = current.current.workspace
    navigate({ tabs: workspace.tabs.filter((tab) => tab.id !== id), activeId: workspace.activeId === id ? catalogId : workspace.activeId })
    setClosing(null)
    requestAnimationFrame(() => document.querySelector<HTMLButtonElement>(`#${workspaceId} [role="tab"][aria-selected="true"]`)?.focus())
  }
  const workspaceId = `${id}-workspace`
  const changed = () => { if (mounted.current) { setVersion((version) => version + 1); invalidateResources() } }
  const actions: WorkspaceActions<S> = { open, loaded, dirty, closed, changed,
    created: (resourceId, name) => { if (mounted.current) { setCreating(false); loaded(resourceId, name); changed(); open(resourceId) } } }
  useEffect(() => { saveWorkspace(storageKey, workspace) }, [storageKey, workspace])
  useEffect(() => { if (route.resourceId === null) navigation.setSearch(resourceRouteParams(navigation.search, workspace), { replace: true }) }, [navigation, route.resourceId, workspace])
  const activeId = route.valid ? workspace.activeId : 'invalid'
  const name = (id: string) => id === createId ? newLabel : names[id] ?? `Resource ${id.slice(0, 8)}`
  return <div id={workspaceId}>
    {!route.valid && <EmptyState title="This resource link is invalid." error><Button onClick={() => navigate({ ...workspace, activeId: catalogId }, true)}>Open catalog</Button></EmptyState>}
    <Tabs id={workspaceId} label={`${label} workspace`} activeId={activeId} onSelect={(activeId) => navigate({ ...workspace, activeId })}
      tabs={[{ id: catalogId, label: `All ${label}` }, ...(creating ? [{ id: createId, label: newLabel, dirty: dirtyTabs[createId], onClose: () => dirtyTabs[createId] ? setClosing(createId) : closed(createId) }] : []),
        ...workspace.tabs.map((tab) => ({ id: tab.id, label: name(tab.id), dirty: dirtyTabs[tab.id], onClose: () => dirtyTabs[tab.id] ? setClosing(tab.id) : closed(tab.id) }))]} />
    <TabPanel id={workspaceId} tabId={catalogId} activeId={activeId}>
      <Catalog actions={{ ...actions, open: (id, section) => { if (id === createId) setCreating(true); open(id, section) } }} version={version} />
    </TabPanel>
    {creating && <TabPanel id={workspaceId} tabId={createId} activeId={activeId}>{tenant.canManage ? <Create actions={actions} active={activeId === createId} /> : <EmptyState title="Tenant viewer access is read-only." />}</TabPanel>}
    {workspace.tabs.map((tab) => <TabPanel key={tab.id} id={workspaceId} tabId={tab.id} activeId={activeId}>
      <Resource id={tab.id} section={tab.section} active={activeId === tab.id} version={version}
        onSection={(section) => navigate({ ...workspace, tabs: workspace.tabs.map((item) => item.id === tab.id ? { ...item, section } : item) })} actions={actions} />
    </TabPanel>)}
    {closing && <ConfirmationDialog title={`Discard changes to ${name(closing)}?`} confirmLabel="Discard and close" onCancel={() => setClosing(null)} onConfirm={() => closed(closing)}>
      Unsaved changes will be lost.</ConfirmationDialog>}
  </div>
}
