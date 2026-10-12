import type { ResourcePlacement } from '../../types'
export function liveBindingPlacements(items: ResourcePlacement[], isolationScopeId: string) {
  const live = items.filter((item) => item.desiredOperation !== 'Delete' && item.status !== 'Deleted')
  return { live, eligible: live.length > 0 && live.every((item) => item.isolationScopeId === isolationScopeId) }
}
export function bindingKeyMappings(aliases: Record<string, string>, mapping: boolean) {
  return mapping ? Object.fromEntries(Object.entries(aliases).filter(([, alias]) => alias)) : {}
}
