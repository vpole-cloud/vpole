import './App.css'
import { useContext } from 'react'
import { CredentialVersionContext } from './auth/credentialVersion'
import { SessionQueries } from './data/SessionQueries'
import { useKeycloak } from '@react-keycloak/web'
import { InvitationPage } from './modules/tenants/InvitationPage'
import { AppShell } from './shell/AppShell'
import { useTenants } from './hooks/useTenants'
import { readIntegrationNavigation } from './modules/platform/integrationsState'
import { appRegistry } from './modules'
import { RealtimeProvider } from './realtime/RealtimeProvider'
import { useRealtime } from './realtime/context'

type RoleClaims = {
  roles?: string[]
  realm_access?: { roles?: string[] }
  resource_access?: Record<string, { roles?: string[] }>
}
const hasPlatformAdminClaim = (claims?: RoleClaims) =>
  [
    ...(claims?.roles ?? []),
    ...(claims?.realm_access?.roles ?? []),
    ...Object.values(claims?.resource_access ?? {}).flatMap((resource) => resource.roles ?? []),
  ].includes('pole-platform-admin')

const initialViewId = readIntegrationNavigation(
  window.location.search,
  appRegistry.views.map((view) => view.id),
).view

function App() {
  useContext(CredentialVersionContext)
  const { keycloak } = useKeycloak()
  const identity = `${import.meta.env.VITE_API_BASE_URL}:${Boolean(keycloak.authenticated)}:${keycloak.subject ?? ''}`
  return (
    <SessionQueries identity={identity}>
      <RealtimeProvider
        baseUrl={import.meta.env.VITE_API_BASE_URL as string}
        token={keycloak.authenticated ? keycloak.token : undefined}
        userId={keycloak.subject}
        platformAdminClaim={hasPlatformAdminClaim(keycloak.tokenParsed as RoleClaims | undefined)}
        enabled={
          Boolean(keycloak.authenticated) && import.meta.env.VITE_REALTIME_ENABLED !== 'false'
        }
      >
        {window.location.pathname === '/invitations' ? (
          <InvitationPage />
        ) : (
          <AuthenticatedWorkspace />
        )}
      </RealtimeProvider>
    </SessionQueries>
  )
}

function AuthenticatedWorkspace() {
  const realtime = useRealtime()
  const { keycloak, initialized } = useKeycloak()
  const authenticated = Boolean(keycloak.authenticated)
  const apiBaseUrl = import.meta.env.VITE_API_BASE_URL as string
  const token = authenticated ? keycloak.token : undefined
  const tenants = useTenants(
    apiBaseUrl,
    token,
    initialized && authenticated,
    keycloak.subject ?? '',
  )
  const claims = keycloak.tokenParsed as RoleClaims | undefined
  return (
    <AppShell
      registry={appRegistry}
      initialViewId={initialViewId}
      tenants={tenants}
      api={{ baseUrl: apiBaseUrl, token }}
      auth={{
        initialized,
        authenticated,
        userId: keycloak.subject ?? '',
        userName: keycloak.tokenParsed?.preferred_username,
        isPlatformAdmin:
          hasPlatformAdminClaim(claims) && (realtime?.platformAllowed(apiBaseUrl) ?? true),
        login: () => {
          void keycloak.login()
        },
        logout: () => {
          void (async () => {
            await realtime?.logout()
            await keycloak.logout()
          })()
        },
      }}
    />
  )
}

export default App
