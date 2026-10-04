import type { AppModule } from '../../shell/contracts'
import { lazy } from 'react'
import { ResourceTree } from '../resource-groups/ResourceTree'
const OverviewView = lazy(() => import('./PlatformViews').then(module => ({ default: module.OverviewView })))
const IntegrationsView = lazy(() => import('./PlatformViews').then(module => ({ default: module.IntegrationsView })))
const StorageView = lazy(() => import('./PlatformViews').then(module => ({ default: module.StorageView })))
const DomainsView = lazy(() => import('../domains/DomainsView').then(module => ({ default: module.DomainsView })))
const ResourceGroupsView = lazy(() => import('../resource-groups/ResourceGroupsView').then(module => ({ default: module.ResourceGroupsView })))

export const overviewModule: AppModule = {
  id: 'overview', views: [{ id: 'overview', label: 'Overview', component: OverviewView, access: 'public' }],
}
export const resourceGroupsModule: AppModule = {
  id: 'resource-groups',
  navigationPanel: ResourceTree,
  views: [{ id: 'resource-groups', label: 'Resource groups', component: ResourceGroupsView, access: 'tenant' }],
  resourceTypes: [{ id: 'pole/resource-group', label: 'Resource group', viewId: 'resource-groups' }],
  commands: [{ id: 'resource-groups.create', label: 'Create resource group', viewId: 'resource-groups', surface: 'palette',
    available: ({ tenant }) => tenant.canManage,
    run: ({ navigation }) => navigation.openResource({ resourceTypeId: 'pole/resource-group', resourceId: 'create' }) }],
}
export const domainsModule: AppModule = {
  id: 'domains', views: [{ id: 'domains', label: 'Domains', component: DomainsView, access: 'tenant' }],
}
export const integrationsModule: AppModule = {
  id: 'integrations', views: [{ id: 'integrations', label: 'Integrations', component: IntegrationsView, access: 'tenant' }],
}
export const storageModule: AppModule = {
  id: 'storage', views: [{ id: 'storage', label: 'Storage', component: StorageView, access: 'authenticated' }],
}
