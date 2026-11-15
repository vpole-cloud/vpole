// @vitest-environment jsdom
import { beforeEach, expect, it, vi } from 'vitest'

beforeEach(() => {
  vi.resetModules()
  sessionStorage.clear()
  window.history.replaceState(null, '', '/')
})
it('removes the capability before identity initialization and keeps StrictMode reads stable', async () => {
  window.history.replaceState(null, '', '/invitations#token=private-capability')
  const first = await import('./invitationHandoff')
  expect(first.initialInvitationToken).toBe('private-capability')
  expect(window.location.hash).toBe('')
  expect(first.safeIdentityRedirect()).not.toContain('private-capability')
  expect((await import('./invitationHandoff')).initialInvitationToken).toBe('private-capability')
  first.clearInvitationHandoff()
  expect(sessionStorage.getItem('pole.invitation.login-handoff')).toBeNull()
})
it('resumes a per-tab login handoff without putting it in an identity redirect', async () => {
  sessionStorage.setItem('pole.invitation.login-handoff', 'original-capability')
  window.history.replaceState(null, '', '/invitations#state=oidc-state&code=oidc-code')
  const handoff = await import('./invitationHandoff')
  expect(handoff.initialInvitationToken).toBe('original-capability')
  expect(handoff.safeIdentityRedirect()).toBe(`${window.location.origin}/invitations`)
  expect(window.location.hash).toContain('oidc-code')
  handoff.clearInvitationHandoff()
  expect(sessionStorage.length).toBe(0)
})
