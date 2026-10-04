import { readResourceRoute, readWorkspace as readStoredWorkspace } from '../../shell/workspaceState'
import type { ResourceTab, ResourceWorkspace } from '../../shell/workspaceState'
export { catalogId, createId, workspaceAtRoute, resourceRouteParams as serviceRouteParams, saveWorkspace } from '../../shell/workspaceState'
export const serviceSections = ['overview', 'configuration', 'dependencies', 'deployments', 'domains', 'builds', 'releases', 'operations'] as const
export type ServiceSection = typeof serviceSections[number]
export type ServiceTab = ResourceTab<ServiceSection>
export type WebServiceWorkspace = ResourceWorkspace<ServiceSection>
export const readServiceRoute = (search: string) => readResourceRoute(search, serviceSections, 'overview')
export const readWorkspace = (storageKey: string) => readStoredWorkspace(storageKey, serviceSections, 'overview')
