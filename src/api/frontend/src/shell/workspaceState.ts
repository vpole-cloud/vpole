// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

export const catalogId = 'catalog'
export const createId = 'create'
export interface ResourceTab<S extends string> {
  id: string
  section: S
  resourcePage?: number
  resourcePageSize?: number
}
export interface ResourceWorkspace<S extends string> {
  tabs: ResourceTab<S>[]
  activeId: string
}

export const validResourceId = (id: string) =>
  id.length > 0 && id.length <= 128 && [...id].every((character) => character.charCodeAt(0) >= 32)
export function positiveParam(
  params: URLSearchParams,
  key: string,
  fallback: number,
  maximum: number,
) {
  const value = Number(params.get(key))
  return Number.isSafeInteger(value) && value > 0 && value <= maximum ? value : fallback
}
export function readResourceRoute<S extends string>(
  search: string,
  sections: readonly S[],
  defaultSection: S,
  defaultPageSize = 8,
) {
  const params = new URLSearchParams(search)
  const resourceId = params.get('resource')
  const section = params.get('section') as S | null
  return {
    resourceId,
    valid: resourceId === null || validResourceId(resourceId),
    section: section && sections.includes(section) ? section : defaultSection,
    pageNumber: positiveParam(params, 'page', 1, 1_000_000),
    pageSize: positiveParam(params, 'pageSize', defaultPageSize, 100),
    search: params.get('search') ?? '',
  }
}
export function workspaceAtRoute<S extends string>(
  current: ResourceWorkspace<S>,
  route: ReturnType<typeof readResourceRoute<S>>,
): ResourceWorkspace<S> {
  if (route.resourceId === null || !route.valid) return current
  const id = route.resourceId
  if (id === catalogId || id === createId) return { ...current, activeId: id }
  return {
    activeId: id,
    tabs: current.tabs.some((tab) => tab.id === id)
      ? current.tabs.map((tab) => (tab.id === id ? { ...tab, section: route.section } : tab))
      : [...current.tabs, { id, section: route.section }],
  }
}
export function resourceRouteParams<S extends string>(
  search: string,
  workspace: ResourceWorkspace<S>,
) {
  const params = new URLSearchParams(search)
  params.set('resource', workspace.activeId)
  const tab = workspace.tabs.find((tab) => tab.id === workspace.activeId)
  if (tab) params.set('section', tab.section)
  else params.delete('section')
  for (const key of ['resourcePage', 'resourcePageSize'] as const) {
    if (tab?.[key]) params.set(key, String(tab[key]))
    else params.delete(key)
  }
  return params
}
export function readWorkspace<S extends string>(
  storageKey: string,
  sections: readonly S[],
  defaultSection: S,
): ResourceWorkspace<S> {
  try {
    const stored: unknown = JSON.parse(localStorage.getItem(storageKey) ?? 'null')
    if (!stored || typeof stored !== 'object' || !('tabs' in stored) || !Array.isArray(stored.tabs))
      return { tabs: [], activeId: catalogId }
    const ids = new Set<string>()
    const tabs: ResourceTab<S>[] = []
    for (const tab of stored.tabs.slice(0, 50)) {
      if (
        !tab ||
        typeof tab.id !== 'string' ||
        !validResourceId(tab.id) ||
        tab.id === catalogId ||
        tab.id === createId ||
        ids.has(tab.id)
      )
        continue
      ids.add(tab.id)
      tabs.push({
        id: tab.id,
        section: sections.includes(tab.section) ? tab.section : defaultSection,
        ...(Number.isSafeInteger(tab.resourcePage) &&
        tab.resourcePage > 0 &&
        tab.resourcePage <= 1_000_000
          ? { resourcePage: tab.resourcePage }
          : {}),
        ...(Number.isSafeInteger(tab.resourcePageSize) &&
        tab.resourcePageSize > 0 &&
        tab.resourcePageSize <= 100
          ? { resourcePageSize: tab.resourcePageSize }
          : {}),
      })
    }
    return {
      tabs,
      activeId:
        'activeId' in stored && tabs.some((tab) => tab.id === stored.activeId)
          ? (stored.activeId as string)
          : catalogId,
    }
  } catch {
    return { tabs: [], activeId: catalogId }
  }
}
export function saveWorkspace<S extends string>(
  storageKey: string,
  workspace: ResourceWorkspace<S>,
) {
  try {
    // Only identities and panel preferences. Form values never enter this representation.
    localStorage.setItem(
      storageKey,
      JSON.stringify({
        tabs: workspace.tabs,
        activeId: workspace.activeId === createId ? catalogId : workspace.activeId,
      }),
    )
  } catch {
    /* The in-memory workspace remains usable without storage. */
  }
}
