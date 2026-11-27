// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { useCallback, useState } from 'react'
import { ReactKeycloakProvider } from '@react-keycloak/web'
import keycloak from './keycloak'
import { CredentialVersionContext } from './credentialVersion'
import App from '../App'
import { safeIdentityRedirect } from '../modules/tenants/invitationHandoff'

// Identity initialization is one document-lifetime configuration. Workspace navigation
// and credential renewal must never change it and restart the provider.
const identityInitOptions = {
  pkceMethod: 'S256' as const,
  onLoad: 'check-sso' as const,
  ...(window.location.pathname === '/invitations' ? { redirectUri: safeIdentityRedirect() } : {}),
}

export function AuthenticatedApp() {
  const [version, setVersion] = useState(0)
  const onTokens = useCallback(() => setVersion((current) => current + 1), [])
  return (
    <ReactKeycloakProvider
      authClient={keycloak}
      onTokens={onTokens}
      initOptions={identityInitOptions}
    >
      <CredentialVersionContext.Provider value={version}>
        <App />
      </CredentialVersionContext.Provider>
    </ReactKeycloakProvider>
  )
}
