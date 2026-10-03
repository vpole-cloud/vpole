export const serviceSections = ['overview', 'configuration', 'deployments', 'builds'] as const
export type ServiceSection = typeof serviceSections[number]
export interface ServiceTab { id: string; section: ServiceSection }
export interface WebServiceWorkspace { tabs: ServiceTab[]; activeId: string }
export const catalogId = 'catalog'
export const createId = 'create'

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
