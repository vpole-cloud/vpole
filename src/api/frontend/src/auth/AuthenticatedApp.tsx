import { useCallback, useState } from 'react'
import { ReactKeycloakProvider } from '@react-keycloak/web'
import keycloak from './keycloak'
import { CredentialVersionContext } from './credentialVersion'
import App from '../App'

export function AuthenticatedApp() {
  const [version, setVersion] = useState(0)
  const onTokens = useCallback(() => setVersion((current) => current + 1), [])
  return <ReactKeycloakProvider authClient={keycloak} onTokens={onTokens}
    initOptions={{ pkceMethod: 'S256', onLoad: 'check-sso' }}>
    <CredentialVersionContext.Provider value={version}><App /></CredentialVersionContext.Provider>
  </ReactKeycloakProvider>
}
