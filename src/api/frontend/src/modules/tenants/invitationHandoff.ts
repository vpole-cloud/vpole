// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

const handoffKey = 'pole.invitation.login-handoff'

function consumeFragment(): string | null {
  if (window.location.pathname !== '/invitations') return null
  const fragment = new URLSearchParams(window.location.hash.slice(1))
  if (!fragment.has('token')) return null
  const token = fragment.get('token') ?? ''
  window.history.replaceState(
    window.history.state,
    '',
    `${window.location.pathname}${window.location.search}`,
  )
  clearInvitationHandoff()
  if (token) saveInvitationHandoff(token)
  return token
}

function captureInvitation() {
  if (window.location.pathname !== '/invitations') return ''
  const linked = consumeFragment()
  if (linked !== null) return linked
  try {
    return sessionStorage.getItem(handoffKey) ?? ''
  } catch {
    return ''
  }
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

export interface InvitationCapability {
  readonly epoch: number
  readonly secret: string
  readonly signal: AbortSignal
}

// Capture before Keycloak.init(). Only an opaque epoch enters React/mutation keys.
let lifetime = new AbortController()
let capability: InvitationCapability = {
  epoch: 0,
  secret: captureInvitation(),
  signal: lifetime.signal,
}
const listeners = new Set<() => void>()
export function captureInvitationFragment() {
  const secret = consumeFragment()
  if (secret === null) return
  // Invalidate callbacks synchronously, including before React commits the replacement view.
  lifetime.abort()
  lifetime = new AbortController()
  capability = { epoch: capability.epoch + 1, secret, signal: lifetime.signal }
  for (const listener of listeners) listener()
}
export const getInvitationCapability = () => capability
export function subscribeInvitationCapability(listener: () => void) {
  listeners.add(listener)
  if (listeners.size === 1) window.addEventListener('hashchange', captureInvitationFragment)
  captureInvitationFragment()
  return () => {
    listeners.delete(listener)
    if (listeners.size === 0) window.removeEventListener('hashchange', captureInvitationFragment)
  }
}
export const safeIdentityRedirect = () =>
  `${window.location.origin}${window.location.pathname}${window.location.search}`
