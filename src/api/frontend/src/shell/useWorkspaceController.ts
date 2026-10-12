import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import { useAppServices } from './AppContext'
import { catalogId, createId, readResourceRoute, readWorkspace, resourceRouteParams, saveWorkspace, workspaceAtRoute, type ResourceTab, type ResourceWorkspace } from './workspaceState'

interface WorkspaceOptions<S extends string> {
  id: string
  sections: readonly S[]
  defaultSection: S
  decorateRoute?: (workspace: ResourceWorkspace<S>, search: string) => ResourceWorkspace<S>
}

// Providers own data and forms; this controller owns references, history and tab lifetime.
export function useWorkspaceController<S extends string>({ id, sections, defaultSection, decorateRoute }: WorkspaceOptions<S>) {
  const { auth, api, navigation } = useAppServices()
  const storageKey = `pole.${id}.workspace.v1.${auth.userId}.${api.tenantId}`
  const route = readResourceRoute(navigation.search, sections, defaultSection)
  const atRoute = (workspace: ResourceWorkspace<S>) => {
    const next = workspaceAtRoute(workspace, route)
    return decorateRoute && route.resourceId !== null && route.valid ? decorateRoute(next, navigation.search) : next
  }
  const [workspace, setWorkspace] = useState(() => atRoute(readWorkspace(storageKey, sections, defaultSection)))
  const [routeSearch, setRouteSearch] = useState(navigation.search)
  const [creating, setCreating] = useState(route.resourceId === createId)
  if (routeSearch !== navigation.search) {
    setRouteSearch(navigation.search)
    setWorkspace((current) => atRoute(current))
    if (route.resourceId === createId) setCreating(true)
  }
  const [names, setNames] = useState<Record<string, string>>({})
  const [dirtyTabs, setDirtyTabs] = useState<Record<string, boolean>>({})
  const [closingId, setClosingId] = useState<string | null>(null)
  const current = useRef({ workspace, navigation })
  const mounted = useRef(false)
  useLayoutEffect(() => { current.current = { workspace, navigation } }, [workspace, navigation])
  useLayoutEffect(() => { mounted.current = true; return () => { mounted.current = false } }, [])
  const navigateWorkspace = useCallback((next: ResourceWorkspace<S>, replace = false) => {
    if (!mounted.current) return
    const navigation = current.current.navigation
    current.current = { workspace: next, navigation }
    setWorkspace(next)
    navigation.setSearch(resourceRouteParams(navigation.search, next), { replace })
  }, [])
  const loaded = useCallback((id: string, name: string) => {
    if (mounted.current) setNames((current) => current[id] === name ? current : { ...current, [id]: name })
  }, [])
  const dirty = useCallback((id: string, value: boolean) => {
    if (mounted.current) setDirtyTabs((current) => current[id] === value ? current : { ...current, [id]: value })
  }, [])
  const open = useCallback((id: string, section?: S) => {
    if (!mounted.current) return
    const workspace = current.current.workspace
    if (id === createId || id === catalogId) {
      if (id === createId) setCreating(true)
      navigateWorkspace({ ...workspace, activeId: id }); return
    }
    navigateWorkspace({ activeId: id, tabs: workspace.tabs.some((tab) => tab.id === id)
      ? workspace.tabs.map((tab) => tab.id === id && section ? { ...tab, section } : tab)
      : [...workspace.tabs, { id, section: section ?? defaultSection }] })
  }, [defaultSection, navigateWorkspace])
  const section = useCallback((id: string, section: S) => {
    const workspace = current.current.workspace
    navigateWorkspace({ ...workspace, tabs: workspace.tabs.map((tab) => tab.id === id ? { ...tab, section } : tab) })
  }, [navigateWorkspace])
  const updateTab = useCallback((id: string, patch: Partial<Omit<ResourceTab<S>, 'id'>>, replace = false) => {
    const workspace = current.current.workspace
    navigateWorkspace({ ...workspace, tabs: workspace.tabs.map((tab) => tab.id === id ? { ...tab, ...patch } : tab) }, replace)
  }, [navigateWorkspace])
  const isMounted = useCallback(() => mounted.current, [])
  const close = useCallback((tabId: string) => {
    if (!mounted.current) return
    if (tabId === createId) setCreating(false)
    const workspace = current.current.workspace
    navigateWorkspace({ tabs: workspace.tabs.filter((tab) => tab.id !== tabId), activeId: workspace.activeId === tabId ? catalogId : workspace.activeId })
    setClosingId(null)
    requestAnimationFrame(() => document.getElementById(`${id}-workspace`)?.querySelector<HTMLButtonElement>('[role="tab"][aria-selected="true"]')?.focus())
  }, [id, navigateWorkspace])
  const requestClose = (id: string) => dirtyTabs[id] ? setClosingId(id) : close(id)
  useEffect(() => { saveWorkspace(storageKey, workspace) }, [storageKey, workspace])
  useEffect(() => {
    if (route.resourceId === null) navigation.setSearch(resourceRouteParams(navigation.search, workspace), { replace: true })
  }, [navigation, route.resourceId, workspace])
  return { route, workspace, creating, setCreating, names, dirtyTabs, closingId, setClosingId,
    isMounted, updateTab, navigateWorkspace, loaded, dirty, open, section, close, requestClose }
}
