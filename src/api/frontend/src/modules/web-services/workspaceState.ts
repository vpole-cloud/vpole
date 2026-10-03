export const serviceSections = ['overview', 'configuration', 'deployments', 'builds'] as const
export type ServiceSection = typeof serviceSections[number]
export interface ServiceTab { id: string; section: ServiceSection }
export interface WebServiceWorkspace { tabs: ServiceTab[]; activeId: string }
export const catalogId = 'catalog'
export const createId = 'create'

export function readServiceRoute(search: string) {
  const params = new URLSearchParams(search)
  const resourceId = params.get('resource')
  const section = params.get('section')
  const integer = (key: string, fallback: number, maximum: number) => {
    const value = Number(params.get(key))
    return Number.isSafeInteger(value) && value > 0 && value <= maximum ? value : fallback
  }
  return {
    resourceId,
    valid: resourceId === null || (resourceId.length > 0 && resourceId.length <= 128 && [...resourceId].every((character) => character.charCodeAt(0) >= 32)),
    section: serviceSections.includes(section as ServiceSection) ? section as ServiceSection : 'overview' as ServiceSection,
    pageNumber: integer('page', 1, 1_000_000),
    pageSize: integer('pageSize', 8, 100),
    search: params.get('search') ?? '',
  }
}

export function workspaceAtRoute(current: WebServiceWorkspace, route: ReturnType<typeof readServiceRoute>): WebServiceWorkspace {
  if (route.resourceId === null || !route.valid) return current
  const id = route.resourceId
  if (id === catalogId || id === createId) return { ...current, activeId: id }
  return { activeId: id, tabs: current.tabs.some((tab) => tab.id === id)
    ? current.tabs.map((tab) => tab.id === id ? { ...tab, section: route.section } : tab)
    : [...current.tabs, { id, section: route.section }] }
}

export function serviceRouteParams(search: string, workspace: WebServiceWorkspace) {
  const params = new URLSearchParams(search)
  params.set('resource', workspace.activeId)
  const section = workspace.tabs.find((tab) => tab.id === workspace.activeId)?.section
  if (section) params.set('section', section)
  else params.delete('section')
  return params
}

export function readWorkspace(storageKey: string): WebServiceWorkspace {
  try {
    const stored: unknown = JSON.parse(localStorage.getItem(storageKey) ?? 'null')
    if (!stored || typeof stored !== 'object' || !('tabs' in stored) || !Array.isArray(stored.tabs)) {
      return { tabs: [], activeId: catalogId }
    }
    const ids = new Set<string>()
    const tabs: ServiceTab[] = []
    for (const tab of stored.tabs.slice(0, 50)) {
      if (!tab || typeof tab.id !== 'string' || !tab.id || tab.id.length > 128 ||
          tab.id === catalogId || tab.id === createId || ids.has(tab.id)) continue
      ids.add(tab.id)
      tabs.push({ id: tab.id, section: serviceSections.includes(tab.section) ? tab.section : 'overview' })
    }
    const activeId = 'activeId' in stored && tabs.some((tab) => tab.id === stored.activeId)
      ? stored.activeId as string : catalogId
    return { tabs, activeId }
  } catch {
    return { tabs: [], activeId: catalogId }
  }
}

export function saveWorkspace(storageKey: string, workspace: WebServiceWorkspace) {
  try {
    // Persist references only. Creation drafts and their form values stay in memory.
    localStorage.setItem(storageKey, JSON.stringify({ tabs: workspace.tabs, activeId: workspace.activeId === createId ? catalogId : workspace.activeId }))
  } catch {
    // Storage may be unavailable or full; the current workspace remains usable.
  }
}
