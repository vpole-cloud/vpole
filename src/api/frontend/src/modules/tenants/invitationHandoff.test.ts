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
  expect(first.getInvitationCapability().secret).toBe('private-capability')
  expect(window.location.hash).toBe('')
  expect(first.safeIdentityRedirect()).not.toContain('private-capability')
  expect((await import('./invitationHandoff')).getInvitationCapability().secret).toBe(
    'private-capability',
  )
  first.clearInvitationHandoff()
  expect(sessionStorage.getItem('pole.invitation.login-handoff')).toBeNull()
})
it('resumes a per-tab login handoff without putting it in an identity redirect', async () => {
  sessionStorage.setItem('pole.invitation.login-handoff', 'original-capability')
  window.history.replaceState(null, '', '/invitations#state=oidc-state&code=oidc-code')
  const handoff = await import('./invitationHandoff')
  expect(handoff.getInvitationCapability().secret).toBe('original-capability')
  expect(handoff.safeIdentityRedirect()).toBe(`${window.location.origin}/invitations`)
  expect(window.location.hash).toContain('oidc-code')
  handoff.clearInvitationHandoff()
  expect(sessionStorage.length).toBe(0)
})

it('replaces a capability with an opaque epoch and immediately aborts the former lifetime', async () => {
  window.history.replaceState(null, '', '/invitations#token=old-capability')
  const handoff = await import('./invitationHandoff')
  const first = handoff.getInvitationCapability()
  const changed = vi.fn()
  const unsubscribe = handoff.subscribeInvitationCapability(changed)
  try {
    window.history.replaceState(null, '', '/invitations#token=new-capability')
    window.dispatchEvent(new HashChangeEvent('hashchange'))
    const current = handoff.getInvitationCapability()
    expect(first.signal.aborted).toBe(true)
    expect(current.secret).toBe('new-capability')
    expect(current.epoch).toBe(first.epoch + 1)
    expect(current.signal.aborted).toBe(false)
    expect(window.location.hash).toBe('')
    expect(changed).toHaveBeenCalledTimes(1)
  } finally {
    unsubscribe()
  }
})
