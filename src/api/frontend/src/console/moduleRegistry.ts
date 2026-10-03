import type { ConsoleModule, ConsoleRegistry } from './contracts'

export function createConsoleRegistry(modules: readonly ConsoleModule[]): ConsoleRegistry {
  const assertUnique = (category: string, entries: readonly { id: string }[]) => {
    const ids = new Set<string>()
    for (const entry of entries) {
      if (!entry.id.trim()) throw new Error(`Console ${category} registration needs an identifier.`)
      if (ids.has(entry.id)) throw new Error(`Duplicate console ${category} identifier: ${entry.id}`)
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
      throw new Error(`Console registration ${contribution.id} references missing view ${contribution.viewId}.`)
    }
  }
  return { views, resourceTypes, commands }
}
