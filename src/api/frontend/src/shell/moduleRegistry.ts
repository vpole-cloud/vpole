import type { AppModule, AppRegistry } from './contracts'

export function createAppRegistry(modules: readonly AppModule[]): AppRegistry {
  const assertUnique = (category: string, entries: readonly { id: string }[]) => {
    const ids = new Set<string>()
    for (const entry of entries) {
      if (!entry.id.trim()) throw new Error(`App ${category} registration needs an identifier.`)
      if (ids.has(entry.id)) throw new Error(`Duplicate app ${category} identifier: ${entry.id}`)
      ids.add(entry.id)
    }
  }

  assertUnique('module', modules)
  const views = modules.flatMap((module) => module.views)
  const resourceTypes = modules.flatMap((module) => module.resourceTypes ?? [])
  const commands = modules.flatMap((module) => module.commands ?? [])
  assertUnique('view', views)
  assertUnique('resource type', resourceTypes)
  assertUnique('command', commands)
  const viewIds = new Set(views.map((view) => view.id))
  for (const contribution of [...resourceTypes, ...commands]) {
    if (!viewIds.has(contribution.viewId)) {
      throw new Error(`App registration ${contribution.id} references missing view ${contribution.viewId}.`)
    }
  }
  return { views, resourceTypes, commands, navigationPanels: modules.flatMap((module) =>
    module.navigationPanel ? [{ id: module.id, component: module.navigationPanel }] : []) }
}
