import './App.css'
import { useKeycloak } from '@react-keycloak/web'
import { useState } from 'react'

function App() {
  const { keycloak, initialized } = useKeycloak()
  const [apiResult, setApiResult] = useState<string>('')
  const [apiError, setApiError] = useState<string>('')

  const handleLogin = () => {
    void keycloak.login()
  }

  const handleLogout = () => {
    void keycloak.logout()
  }

  const callProtectedApi = async () => {
    setApiResult('')
    setApiError('')

    if (!keycloak.authenticated) {
      setApiError('Not authenticated. Please log in first.')
      return
    }

    const response = await fetch(
      `${import.meta.env.VITE_API_BASE_URL as string}/test-keycloak`,
      {
        headers: {
          Authorization: `Bearer ${keycloak.token}`,
        },
      },
    )

    if (!response.ok) {
      const text = await response.text()
      setApiError(`API error (${response.status}): ${text}`)
      return
    }

    const text = await response.text()
    setApiResult(text)
  }

  return (
    <>
      <h1>Control plane</h1>
      {!initialized && <p>Initializing Keycloak...</p>}
      {initialized && (
        <div>
          <p>Status: {keycloak.authenticated ? 'Authenticated' : 'Guest'}</p>
          <div>
            <button onClick={handleLogin} disabled={keycloak.authenticated}>
              Log in
            </button>
            <button onClick={handleLogout} disabled={!keycloak.authenticated}>
              Log out
            </button>
            <button onClick={() => void callProtectedApi()}>
              Call /test-keycloak
            </button>
          </div>
          {apiResult && <p>API response: {apiResult}</p>}
          {apiError && <p>API error: {apiError}</p>}
        </div>
      )}
    </>
  )
}

export default App
