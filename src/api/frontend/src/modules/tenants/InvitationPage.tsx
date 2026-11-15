import { useEffect, useLayoutEffect, useState, useSyncExternalStore } from 'react'
import { useKeycloak } from '@react-keycloak/web'
import { ApiError } from '../../api/transport'
import { useScopedMutation } from '../../hooks/useScopedMutation'
import { Button, EmptyState, Notice } from '../../ui/components'
import { acceptTenantInvitation, invitationAction, previewTenantInvitation } from './api'
import type { InvitationPreview } from './types'
import {
  clearInvitationHandoff,
  captureInvitationFragment,
  getInvitationCapability,
  subscribeInvitationCapability,
  type InvitationCapability,
  saveInvitationHandoff,
} from './invitationHandoff'

export function InvitationPage() {
  const capability = useSyncExternalStore(subscribeInvitationCapability, getInvitationCapability)
  useLayoutEffect(captureInvitationFragment, [])
  return <InvitationContent key={capability.epoch} capability={capability} />
}

function InvitationContent({ capability }: { capability: InvitationCapability }) {
  const { keycloak, initialized } = useKeycloak()
  const secret = capability.secret
  const [preview, setPreview] = useState<InvitationPreview | null>(null)
  const [unavailable, setUnavailable] = useState(false)
  const [message, setMessage] = useState('')
  const [previewError, setPreviewError] = useState('')
  const [previewAttempt, setPreviewAttempt] = useState(0)
  const baseUrl = import.meta.env.VITE_API_BASE_URL as string
  const mutation = useScopedMutation(
    `invitation:${capability.epoch}:${keycloak.subject ?? 'guest'}`,
  )
  useEffect(() => {
    if (!secret || capability.signal.aborted) return
    const controller = new AbortController()
    const signal = AbortSignal.any([controller.signal, capability.signal])
    void previewTenantInvitation(baseUrl, secret, signal)
      .then((value) => {
        if (!signal.aborted) setPreview(value)
      })
      .catch((error: unknown) => {
        if (signal.aborted) return
        if (error instanceof ApiError && error.status === 410) setUnavailable(true)
        else setPreviewError('Unable to load the invitation. Please try again.')
      })
    return () => controller.abort()
  }, [baseUrl, secret, previewAttempt, capability.signal])
  useEffect(() => {
    if (initialized) clearInvitationHandoff()
  }, [initialized])
  const setup = () => {
    if (capability.signal.aborted) return
    void mutation
      .run(
        (signal) =>
          invitationAction<{ setupEmailSent: boolean }>(
            baseUrl,
            'onboarding',
            secret,
            undefined,
            AbortSignal.any([signal, capability.signal]),
          ),
        (result) => {
          if (capability.signal.aborted) return
          setMessage(
            result.setupEmailSent
              ? 'Check your email to finish account setup or verify your email. Then open your Pole invitation email to continue.'
              : 'Your account is ready. Sign in to accept this invitation.',
          )
        },
      )
      .catch(() => {})
  }
  const signIn = () => {
    if (capability.signal.aborted) return
    saveInvitationHandoff(secret)
    void keycloak.login({ redirectUri: `${window.location.origin}/invitations` })
  }
  const accept = () => {
    if (capability.signal.aborted) return
    void mutation
      .run(
        (signal) =>
          acceptTenantInvitation(
            baseUrl,
            secret,
            keycloak.token,
            AbortSignal.any([signal, capability.signal]),
          ),
        (result) => {
          if (capability.signal.aborted) return
          try {
            localStorage.setItem('pole.activeTenantId', result.tenantId)
          } catch {
            /* Optional preference. */
          }
          window.location.replace(`/?tenant=${encodeURIComponent(result.tenantId)}`)
        },
      )
      .catch(() => {})
  }
  const email = keycloak.tokenParsed?.email as string | undefined
  const verified = keycloak.tokenParsed?.email_verified === true
  const identityMatches = verified && email?.trim().toLowerCase() === preview?.email.toLowerCase()
  return (
    <main className="app-shell__content" aria-label="Tenant invitation">
      <h1>{unavailable ? 'Invitation unavailable' : 'Join a tenant'}</h1>
      {!secret ? (
        <Notice>Open your Pole invitation email to continue.</Notice>
      ) : unavailable ? (
        <Notice tone="error">
          This invitation has expired, was revoked, or has already been used. Ask the tenant
          administrator for a new invitation.
        </Notice>
      ) : previewError ? (
        <Notice tone="error">
          <p>{previewError}</p>
          <Button
            onClick={() => {
              setPreviewError('')
              setPreviewAttempt((attempt) => attempt + 1)
            }}
          >
            Retry preview
          </Button>
        </Notice>
      ) : !preview ? (
        <EmptyState title="Loading invitation…" />
      ) : (
        <>
          <p>
            You are invited to <strong>{preview.tenantName}</strong> as {preview.role}.
          </p>
          <p>Invited email: {preview.email}</p>
          <p>Expires: {new Date(preview.expiresAtUtc).toLocaleString()}</p>
          {message && <Notice>{message}</Notice>}
          {Boolean(mutation.error) && (
            <Notice tone="error">
              {mutation.error instanceof Error
                ? mutation.error.message
                : 'Unable to complete this action.'}
            </Notice>
          )}
          <div className="row-actions">
            {!keycloak.authenticated && (
              <>
                <Button disabled={mutation.busy || !initialized} onClick={setup}>
                  Set up account or verify email
                </Button>
                <Button disabled={mutation.busy || !initialized} onClick={signIn}>
                  Sign in
                </Button>
              </>
            )}
            {keycloak.authenticated && (
              <>
                {!identityMatches && (
                  <Notice tone="error">
                    Sign in with the verified invited email address. You are signed in as{' '}
                    {email ?? 'another account'}.
                  </Notice>
                )}
                {!verified && email?.toLowerCase() === preview.email.toLowerCase() && (
                  <Button disabled={mutation.busy} onClick={setup}>
                    Set up account or verify email
                  </Button>
                )}
                <Button
                  variant="primary"
                  disabled={mutation.busy || !identityMatches}
                  onClick={accept}
                >
                  Accept invitation
                </Button>
                <Button
                  disabled={mutation.busy}
                  onClick={() => {
                    if (capability.signal.aborted) return
                    saveInvitationHandoff(secret)
                    void keycloak.logout({ redirectUri: `${window.location.origin}/invitations` })
                  }}
                >
                  Use another account
                </Button>
              </>
            )}
          </div>
        </>
      )}
    </main>
  )
}
