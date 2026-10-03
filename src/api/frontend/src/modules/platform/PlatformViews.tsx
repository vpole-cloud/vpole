import { useEffect, useState } from 'react'
import { OverviewPage } from '../../components/OverviewPage'
import { DomainZonesPage } from '../../components/DomainZonesPage'
import { IntegrationsPage } from '../../components/IntegrationsPage'
import { StorageSettingsPage } from '../../components/StorageSettingsPage'
import { useOverviewData } from '../../hooks/useOverviewData'
import { useDomainZonesManager } from '../../hooks/useDomainZonesManager'
import { useGitIntegrationsManager } from '../../hooks/useGitIntegrationsManager'
import { useStorageSettingsManager } from '../../hooks/useStorageSettingsManager'
import { readIntegrationNavigation } from '../../integrationsState'
import { useAppServices } from '../../shell/AppContext'

function useTenantApiOptions() {
  const { api, tenant } = useAppServices()
  return { apiBaseUrl: api.baseUrl, token: api.token, tenantId: api.tenantId, enabled: Boolean(tenant.active) }
}

export function OverviewView() {
  const { auth, tenant, resourceTypes, navigation } = useAppServices()
  const overview = useOverviewData(useTenantApiOptions())
  return <><OverviewPage authState={auth}
    onValidateIdentity={() => { void overview.runIdentityCheck() }} overview={overview} />
    {tenant.active && <section className="panel" aria-label="Resource catalogs"><h2 className="panel__title">Resource catalogs</h2>
      <p className="field__hint">Open a resource catalog in the active tenant.</p><div className="row-actions">
        {resourceTypes.map((type) => <a key={type.id} className="button" href={navigation.resourceHref({ resourceTypeId: type.id, resourceId: 'catalog' })}>{type.label}</a>)}
      </div></section>}
    </>
}

export function DomainsView() {
  const { tenant } = useAppServices()
  const zones = useDomainZonesManager(useTenantApiOptions())
  return <DomainZonesPage {...zones} onRefresh={zones.refresh} onCreate={zones.create}
    onDelete={zones.remove} canManage={tenant.canManage} />
}

export function IntegrationsView() {
  const { tenant, navigation: { search, setSearch } } = useAppServices()
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
  const { api, auth } = useAppServices()
  const storage = useStorageSettingsManager({ apiBaseUrl: api.baseUrl, token: api.token, enabled: auth.authenticated })
  return <StorageSettingsPage {...storage} onRefresh={storage.refresh} onCreate={storage.create}
    onUpdate={storage.update} canManage={auth.isPlatformAdmin || storage.status === 'success'} />
}
