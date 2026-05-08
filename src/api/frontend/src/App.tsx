import './App.css'
import { useKeycloak } from '@react-keycloak/web'
import { useState } from 'react'
import { AppShell } from './components/AppShell'
import { OverviewPage } from './components/OverviewPage'
import { ResourceGroupsPage } from './components/ResourceGroupsPage'
import { SideNav } from './components/SideNav'
import { TopBandPlaceholder } from './components/TopBandPlaceholder'
import { WebServicesPage } from './components/WebServicesPage'
import { useOverviewData } from './hooks/useOverviewData'
import { useResourceGroupsManager } from './hooks/useResourceGroupsManager'
import { useWebServicesManager } from './hooks/useWebServicesManager'
import type { AppView } from './types'

function App() {
  const { keycloak, initialized } = useKeycloak()
  const [currentView, setCurrentView] = useState<AppView>('overview')
  const overview = useOverviewData({
    apiBaseUrl: import.meta.env.VITE_API_BASE_URL as string,
    token: keycloak.authenticated ? keycloak.token : undefined,
    enabled: initialized && currentView === 'overview',
  })
  const resourceGroups = useResourceGroupsManager({
    apiBaseUrl: import.meta.env.VITE_API_BASE_URL as string,
    token: keycloak.authenticated ? keycloak.token : undefined,
    enabled:
      initialized &&
      (currentView === 'resource-groups' || currentView === 'web-services'),
  })
  const webServices = useWebServicesManager({
    apiBaseUrl: import.meta.env.VITE_API_BASE_URL as string,
    token: keycloak.authenticated ? keycloak.token : undefined,
    enabled: initialized && currentView === 'web-services',
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
      topBand={<TopBandPlaceholder />}
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
        />
      ) : (
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
        />
      )}
    </AppShell>
  )
}

export default App
