import './App.css'
import { useKeycloak } from '@react-keycloak/web'
import { useState } from 'react'
import { AppShell } from './components/AppShell'
import { OverviewPage } from './components/OverviewPage'
import { ResourceGroupsPage } from './components/ResourceGroupsPage'
import { SideNav } from './components/SideNav'
import { StorageSettingsPage } from './components/StorageSettingsPage'
import { TopBandPlaceholder } from './components/TopBandPlaceholder'
import { WebServicesPage } from './components/WebServicesPage'
import { useOverviewData } from './hooks/useOverviewData'
import { useResourceGroupsManager } from './hooks/useResourceGroupsManager'
import { useStorageSettingsManager } from './hooks/useStorageSettingsManager'
import { useTenants } from './hooks/useTenants'
import { useWebServicesManager } from './hooks/useWebServicesManager'
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

function App() {
  const { keycloak, initialized } = useKeycloak()
  const [currentView, setCurrentView] = useState<AppView>('overview')
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
  const storageSettings = useStorageSettingsManager({
    apiBaseUrl,
    token,
    enabled: initialized && currentView === 'storage' && Boolean(keycloak.authenticated),
  })

  const handleLogin = () => {
    void keycloak.login()
  }

  const handleLogout = () => {
    void keycloak.logout()
  }

  const handleValidateIdentity = () => {
    void overview.runIdentityCheck()
  }

  return (
    <AppShell
      sideNav={
        <SideNav
          authenticated={Boolean(keycloak.authenticated)}
          currentView={currentView}
          onSelectView={setCurrentView}
          userName={keycloak.tokenParsed?.preferred_username}
        />
      }
      topBand={
        <TopBandPlaceholder
          tenants={tenants.items}
          activeTenantId={tenants.activeTenantId}
          status={tenants.status}
          error={tenants.error}
          onSelectTenant={tenants.selectTenant}
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
