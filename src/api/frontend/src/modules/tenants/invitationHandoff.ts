const handoffKey = 'pole.invitation.login-handoff'

// Runs before Keycloak.init(). Capabilities never enter OIDC redirect URIs, HTTP URLs,
// query keys or React keys. One module value also survives StrictMode's repeated mount.
function captureInvitation() {
  if (window.location.pathname !== '/invitations') return ''
  const linked = new URLSearchParams(window.location.hash.slice(1)).get('token')
  let pending: string | null = null
  try {
    pending = sessionStorage.getItem(handoffKey)
  } catch {
    /* Optional login handoff. */
  }
  if (linked) {
    window.history.replaceState(
      window.history.state,
      '',
      `${window.location.pathname}${window.location.search}`,
    )
    saveInvitationHandoff(linked)
  }
  return linked ?? pending ?? ''
}
export function saveInvitationHandoff(token: string) {
  try {
    sessionStorage.setItem(handoffKey, token)
  } catch {
    /* Reopen original email after login. */
  }
}
export function clearInvitationHandoff() {
  try {
    sessionStorage.removeItem(handoffKey)
  } catch {
    /* Optional storage. */
  }
}
export const initialInvitationToken = captureInvitation()
export const safeIdentityRedirect = () =>
  `${window.location.origin}${window.location.pathname}${window.location.search}`
