import type { AppModule } from '../../shell/contracts'
import { OverviewView, ResourceGroupsView, DomainsView, IntegrationsView, StorageView } from './PlatformViews'

export const overviewModule: AppModule = {
  id: 'overview', views: [{ id: 'overview', label: 'Overview', component: OverviewView, access: 'public' }],
}
export const resourceGroupsModule: AppModule = {
  id: 'resource-groups',
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
