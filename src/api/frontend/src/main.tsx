import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { ReactKeycloakProvider } from '@react-keycloak/web'
import './index.css'
import App from './App.tsx'
import keycloak from './auth/keycloak'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <ReactKeycloakProvider
      authClient={keycloak}
      initOptions={{ pkceMethod: 'S256', onLoad: 'check-sso' }}
    >
      <App />
    </ReactKeycloakProvider>
  </StrictMode>,
)
