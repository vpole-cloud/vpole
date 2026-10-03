import './App.css'
import { useKeycloak } from '@react-keycloak/web'
import { AppShell } from './shell/AppShell'
import { useTenants } from './hooks/useTenants'
import { readIntegrationNavigation } from './integrationsState'
import { appRegistry } from './modules'

type RoleClaims = {
  roles?: string[]
  realm_access?: { roles?: string[] }
  resource_access?: Record<string, { roles?: string[] }>
}

const initialViewId = readIntegrationNavigation(
  window.location.search, appRegistry.views.map((view) => view.id),
).view

function App() {
  const { keycloak, initialized } = useKeycloak()
  const authenticated = Boolean(keycloak.authenticated)
  const apiBaseUrl = import.meta.env.VITE_API_BASE_URL as string
  const token = authenticated ? keycloak.token : undefined
  const tenants = useTenants(apiBaseUrl, token, initialized && authenticated)
  const claims = keycloak.tokenParsed as RoleClaims | undefined
  const roles = [
    ...(claims?.roles ?? []), ...(claims?.realm_access?.roles ?? []),
    ...Object.values(claims?.resource_access ?? {}).flatMap((resource) => resource.roles ?? []),
  ]
  return <AppShell registry={appRegistry} initialViewId={initialViewId} tenants={tenants}
    api={{ baseUrl: apiBaseUrl, token }} auth={{
      initialized, authenticated, userId: keycloak.subject ?? '',
      userName: keycloak.tokenParsed?.preferred_username, isPlatformAdmin: roles.includes('pole-platform-admin'),
      login: () => { void keycloak.login() }, logout: () => { void keycloak.logout() },
    }} />
}

export default App
