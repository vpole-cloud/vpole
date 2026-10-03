import type { ConsoleModule } from '../../console/contracts'
import { OverviewView, ResourceGroupsView, DomainsView, IntegrationsView, StorageView } from './PlatformViews'

export const overviewModule: ConsoleModule = {
  id: 'overview', views: [{ id: 'overview', label: 'Overview', component: OverviewView, access: 'public' }],
}
export const resourceGroupsModule: ConsoleModule = {
  id: 'resource-groups',
  views: [{ id: 'resource-groups', label: 'Resource groups', component: ResourceGroupsView, access: 'tenant' }],
  resourceTypes: [{ id: 'pole/resource-group', label: 'Resource group', viewId: 'resource-groups' }],
}
export const domainsModule: ConsoleModule = {
  id: 'domains', views: [{ id: 'domains', label: 'Domains', component: DomainsView, access: 'tenant' }],
}
export const integrationsModule: ConsoleModule = {
  id: 'integrations', views: [{ id: 'integrations', label: 'Integrations', component: IntegrationsView, access: 'tenant' }],
}
export const storageModule: ConsoleModule = {
  id: 'storage', views: [{ id: 'storage', label: 'Storage', component: StorageView, access: 'authenticated' }],
}
