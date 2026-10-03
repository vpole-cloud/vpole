import { useEffect, useState } from 'react'
import { OverviewPage } from '../../components/OverviewPage'
import { ResourceGroupsPage } from '../../components/ResourceGroupsPage'
import { DomainZonesPage } from '../../components/DomainZonesPage'
import { IntegrationsPage } from '../../components/IntegrationsPage'
import { StorageSettingsPage } from '../../components/StorageSettingsPage'
import { useOverviewData } from '../../hooks/useOverviewData'
import { useResourceGroupsManager } from '../../hooks/useResourceGroupsManager'
import { useDomainZonesManager } from '../../hooks/useDomainZonesManager'
import { useGitIntegrationsManager } from '../../hooks/useGitIntegrationsManager'
import { useStorageSettingsManager } from '../../hooks/useStorageSettingsManager'
import { readIntegrationNavigation } from '../../integrationsState'
import { useConsoleServices } from '../../console/ConsoleContext'

function useTenantApiOptions() {
  const { api, tenant } = useConsoleServices()
  return { apiBaseUrl: api.baseUrl, token: api.token, tenantId: api.tenantId, enabled: Boolean(tenant.active) }
}

export function OverviewView() {
  const { auth } = useConsoleServices()
  const overview = useOverviewData(useTenantApiOptions())
  return <OverviewPage authState={auth}
    onValidateIdentity={() => { void overview.runIdentityCheck() }} overview={overview} />
}

export function ResourceGroupsView() {
  const { tenant } = useConsoleServices()
  const groups = useResourceGroupsManager(useTenantApiOptions())
  return <ResourceGroupsPage {...groups} onRefresh={groups.refresh} onCreate={groups.create}
    onUpdate={groups.update} onDelete={groups.remove} canManage={tenant.canManage} />
}

export function DomainsView() {
  const { tenant } = useConsoleServices()
  const zones = useDomainZonesManager(useTenantApiOptions())
  return <DomainZonesPage {...zones} onRefresh={zones.refresh} onCreate={zones.create}
    onDelete={zones.remove} canManage={tenant.canManage} />
}

export function IntegrationsView() {
  const { tenant, navigation: { search, setSearch } } = useConsoleServices()
  const [notice, setNotice] = useState(() => readIntegrationNavigation(window.location.search).githubNotice)
  const manager = useGitIntegrationsManager({ ...useTenantApiOptions(),
    preferredConnectionId: notice?.status === 'connected' ? notice.connectionId : undefined })
  useEffect(() => {
    const params = new URLSearchParams(search)
    if (!['github', 'connectionId', 'reason'].some((key) => params.has(key))) return
    for (const key of ['github', 'connectionId', 'reason']) params.delete(key)
    setSearch(params, { replace: true })
  }, [search, setSearch])
  return <IntegrationsPage manager={manager} notice={notice}
    onDismissNotice={() => setNotice(null)} canManage={tenant.canManage} />
}

export function StorageView() {
  const { api, auth } = useConsoleServices()
  const storage = useStorageSettingsManager({ apiBaseUrl: api.baseUrl, token: api.token, enabled: auth.authenticated })
  return <StorageSettingsPage {...storage} onRefresh={storage.refresh} onCreate={storage.create}
    onUpdate={storage.update} canManage={auth.isPlatformAdmin || storage.status === 'success'} />
}
