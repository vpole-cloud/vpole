import { readResourceRoute, readWorkspace as readStoredWorkspace } from '../../shell/workspaceState'
import type { ResourceWorkspace } from '../../shell/workspaceState'
export const groupSections = ['resources', 'operations', 'configuration'] as const
export type GroupSection = typeof groupSections[number]
export type GroupWorkspace = ResourceWorkspace<GroupSection>
export const readGroupRoute = (search: string) => readResourceRoute(search, groupSections, 'resources')
export const readWorkspace = (storageKey: string) => readStoredWorkspace(storageKey, groupSections, 'resources')
