import './App.css'
import { useKeycloak } from '@react-keycloak/web'
import { useEffect, useState } from 'react'
import { AppShell } from './components/AppShell'
import { OverviewPage } from './components/OverviewPage'
import { IntegrationsPage } from './components/IntegrationsPage'
import { ResourceGroupsPage } from './components/ResourceGroupsPage'
import { SideNav } from './components/SideNav'
import { StorageSettingsPage } from './components/StorageSettingsPage'
import { TopBandPlaceholder } from './components/TopBandPlaceholder'
import { WebServicesPage } from './components/WebServicesPage'
import { DomainZonesPage } from './components/DomainZonesPage'
import { useOverviewData } from './hooks/useOverviewData'
import { useGitIntegrationsManager } from './hooks/useGitIntegrationsManager'
import { useResourceGroupsManager } from './hooks/useResourceGroupsManager'
import { useStorageSettingsManager } from './hooks/useStorageSettingsManager'
import { useSourceBuildsManager } from './hooks/useSourceBuildsManager'
import { useTenants } from './hooks/useTenants'
import { useWebServicesManager } from './hooks/useWebServicesManager'
import { useDomainZonesManager } from './hooks/useDomainZonesManager'
import {
  clearTransientIntegrationParams,
  readIntegrationNavigation,
} from './integrationsState'
import type { AppView } from './types'

type KeycloakRoleClaims = {
  roles?: string[]
  realm_access?: {
    roles?: string[]
  }
  resource_access?: Record<
    string,
    {
      roles?: string[]
    }
  >
}

const hasPlatformAdminRole = (claims?: KeycloakRoleClaims) => {
  const roles = new Set([
    ...(claims?.roles ?? []),
    ...(claims?.realm_access?.roles ?? []),
    ...Object.values(claims?.resource_access ?? {}).flatMap(
      (resource) => resource.roles ?? [],
    ),
  ])

  return roles.has('pole-platform-admin')
}

const initialNavigation = readIntegrationNavigation(window.location.search)

function App() {
  const { keycloak, initialized } = useKeycloak()
  const [currentView, setCurrentView] = useState<AppView>(initialNavigation.view)
  const [integrationNotice, setIntegrationNotice] = useState(
    initialNavigation.githubNotice,
  )
  const [selectedWebServiceId, setSelectedWebServiceId] = useState<string | null>(null)
  const apiBaseUrl = import.meta.env.VITE_API_BASE_URL as string
  const token = keycloak.authenticated ? keycloak.token : undefined
  const isPlatformAdmin = hasPlatformAdminRole(
    keycloak.tokenParsed as KeycloakRoleClaims | undefined,
  )
  const tenants = useTenants(apiBaseUrl, token, initialized && Boolean(keycloak.authenticated))
  const tenantReady = Boolean(tenants.activeTenantId)
  const canManageTenantResources =
    tenants.activeTenant?.role === 'Owner' ||
    tenants.activeTenant?.role === 'Admin' ||
    tenants.activeTenant?.role === 'Member'
  const overview = useOverviewData({
    apiBaseUrl,
    token,
    tenantId: tenants.activeTenantId,
    enabled: initialized && currentView === 'overview' && tenantReady,
  })
  const resourceGroups = useResourceGroupsManager({
    apiBaseUrl,
    token,
    tenantId: tenants.activeTenantId,
    enabled:
      initialized &&
      tenantReady &&
      (currentView === 'resource-groups' || currentView === 'web-services'),
  })
  const webServices = useWebServicesManager({
    apiBaseUrl,
    token,
    tenantId: tenants.activeTenantId,
    enabled: initialized && currentView === 'web-services' && tenantReady,
  })
  const domainZones = useDomainZonesManager({
    apiBaseUrl,
    token,
    tenantId: tenants.activeTenantId,
    enabled:
      initialized &&
      tenantReady &&
      (currentView === 'domains' || currentView === 'web-services'),
  })
  const storageSettings = useStorageSettingsManager({
    apiBaseUrl,
    token,
    enabled: initialized && currentView === 'storage' && Boolean(keycloak.authenticated),
  })
  const sourceBuilds = useSourceBuildsManager({
    apiBaseUrl,
    token,
    tenantId: tenants.activeTenantId,
    webServiceId: selectedWebServiceId,
    enabled: initialized && currentView === 'web-services' && tenantReady,
  })
  const integrations = useGitIntegrationsManager({
    apiBaseUrl,
    token,
    tenantId: tenants.activeTenantId,
    enabled:
      initialized &&
      currentView === 'integrations' &&
      tenantReady &&
      Boolean(keycloak.authenticated),
    preferredConnectionId:
      integrationNotice?.status === 'connected'
        ? integrationNotice.connectionId
        : undefined,
  })

  useEffect(() => {
    if (initialNavigation.githubNotice) {
      window.history.replaceState(
        {},
        '',
        clearTransientIntegrationParams(window.location.href),
      )
    }
  }, [])

  const handleLogin = () => {
    void keycloak.login()
  }

  const handleLogout = () => {
    void keycloak.logout()
  }

  const handleValidateIdentity = () => {
    void overview.runIdentityCheck()
  }

  const handleSelectView = (view: AppView) => {
    setCurrentView(view)
    const url = new URL(window.location.href)
    if (view === 'overview') {
      url.searchParams.delete('view')
    } else {
      url.searchParams.set('view', view)
    }
    window.history.replaceState({}, '', clearTransientIntegrationParams(url.toString()))
  }

  return (
    <AppShell
      sideNav={
        <SideNav
          authenticated={Boolean(keycloak.authenticated)}
          currentView={currentView}
          onSelectView={handleSelectView}
          userName={keycloak.tokenParsed?.preferred_username}
        />
      }
      topBand={
        <TopBandPlaceholder
          tenants={tenants.items}
          activeTenantId={tenants.activeTenantId}
          status={tenants.status}
          error={tenants.error}
          onSelectTenant={(tenantId) => {
            setSelectedWebServiceId(null)
            tenants.selectTenant(tenantId)
          }}
        />
      }
    >
      {currentView === 'overview' ? (
        <OverviewPage
          authState={{
            initialized,
            authenticated: Boolean(keycloak.authenticated),
            userName: keycloak.tokenParsed?.preferred_username,
          }}
          onLogin={handleLogin}
          onLogout={handleLogout}
          onValidateIdentity={handleValidateIdentity}
          overview={overview}
        />
      ) : currentView === 'resource-groups' ? (
        <ResourceGroupsPage
          items={resourceGroups.items}
          status={resourceGroups.status}
          error={resourceGroups.error}
          submitStatus={resourceGroups.submitStatus}
          submitError={resourceGroups.submitError}
          deletingId={resourceGroups.deletingId}
          onRefresh={resourceGroups.refresh}
          onCreate={resourceGroups.create}
          onUpdate={resourceGroups.update}
          onDelete={resourceGroups.remove}
          canManage={canManageTenantResources}
        />
      ) : currentView === 'web-services' ? (
        <WebServicesPage
          items={webServices.items}
          resourceGroups={resourceGroups.items}
          status={webServices.status}
          error={webServices.error}
          submitStatus={webServices.submitStatus}
          submitError={webServices.submitError}
          deletingId={webServices.deletingId}
          onRefresh={webServices.refresh}
          onCreate={webServices.create}
          onUpdate={webServices.update}
          onDelete={webServices.remove}
          selectedId={selectedWebServiceId}
          onSelect={setSelectedWebServiceId}
          sourceBuilds={sourceBuilds}
          canManage={canManageTenantResources}
          availableTargets={webServices.availableTargets}
          targetsStatus={webServices.targetsStatus}
          targetsError={webServices.targetsError}
          computeProfilesByTarget={webServices.computeProfilesByTarget}
          zones={domainZones.items}
          onLoadTargets={webServices.loadAvailableTargets}
          onLoadComputeProfiles={webServices.loadComputeProfiles}
          onLoadHostnameBindings={webServices.loadHostnameBindings}
          onLoadReleases={webServices.loadReleases}
          hostnameBindingsByDeployment={webServices.hostnameBindingsByDeployment}
          releasesByWebService={webServices.releasesByWebService}
          onAddDeployment={webServices.addDeployment}
          onUpdateDeployment={webServices.updateDeployment}
          onRemoveDeployment={webServices.removeDeployment}
          onRetryDeployment={webServices.retryDeployment}
          onAddHostname={webServices.addHostname}
          onRemoveHostname={webServices.removeHostname}
        />
      ) : currentView === 'domains' ? (
        <DomainZonesPage
          items={domainZones.items}
          targets={domainZones.targets}
          status={domainZones.status}
          error={domainZones.error}
          submitStatus={domainZones.submitStatus}
          submitError={domainZones.submitError}
          onRefresh={domainZones.refresh}
          onCreate={domainZones.create}
          onDelete={domainZones.remove}
          canManage={canManageTenantResources}
        />
      ) : currentView === 'integrations' ? (
        <IntegrationsPage
          manager={integrations}
          notice={integrationNotice}
          onDismissNotice={() => setIntegrationNotice(null)}
          canManage={canManageTenantResources}
        />
      ) : (
        <StorageSettingsPage
          profiles={storageSettings.profiles}
          storageClasses={storageSettings.storageClasses}
          persistentVolumes={storageSettings.persistentVolumes}
          persistentVolumeClaims={storageSettings.persistentVolumeClaims}
          status={storageSettings.status}
          error={storageSettings.error}
          submitStatus={storageSettings.submitStatus}
          submitError={storageSettings.submitError}
          onRefresh={storageSettings.refresh}
          onCreate={storageSettings.create}
          onUpdate={storageSettings.update}
          canManage={isPlatformAdmin || storageSettings.status === 'success'}
        />
      )}
    </AppShell>
  )
}

export default App
