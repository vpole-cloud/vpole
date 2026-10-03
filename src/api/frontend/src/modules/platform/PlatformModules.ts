import type { AppModule } from '../../shell/contracts'
import { OverviewView, DomainsView, IntegrationsView, StorageView } from './PlatformViews'
import { ResourceGroupsView } from '../resource-groups/ResourceGroupsView'
import { ResourceTree } from '../resource-groups/ResourceTree'

export const overviewModule: AppModule = {
  id: 'overview', views: [{ id: 'overview', label: 'Overview', component: OverviewView, access: 'public' }],
}
export const resourceGroupsModule: AppModule = {
  id: 'resource-groups',
  navigationPanel: ResourceTree,
  views: [{ id: 'resource-groups', label: 'Resource groups', component: ResourceGroupsView, access: 'tenant' }],
  resourceTypes: [{ id: 'pole/resource-group', label: 'Resource group', viewId: 'resource-groups' }],
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
