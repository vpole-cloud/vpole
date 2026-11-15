# Tenant membership and invitations

Tenants owns membership, invitation lifecycle, acceptance receipts and access-change hints.
Users owns verified identity persistence and default-tenant selection through its caller-session
Contracts. No tenant header is needed for discovery or invitation acceptance. Member management
uses the route tenant and ignores the active-tenant header.

Tenant Owners and Admins invite by email. Admins can grant Viewer, Member or Admin; only Owners
can grant or manage Owner invitations. Invitation authority requires current tenant membership,
including for a platform administrator. Existing platform-administration and member-management
operations retain their documented platform-admin authorization.

`POST /api/v1/tenants/{tenantId}/invitations` creates a seven-day invitation. Each tenant and
normalized email has one durable slot, with at most one active invitation. A duplicate active
invitation returns 409. List invitations with GET on the same route; POST `/{id}/resend` rotates
the secret and expiry, POST `/{id}/revoke` invalidates it, and PUT `/{id}` changes its role and
sends a replacement link. Only authorized managers can read this list. The Members screen shows
current members, pending/expired invitations, delivery state and last-owner protection.

There is no direct-add member endpoint or implementation. New and existing identities must use
invitations. An invitation alone never grants access. Opening `/invitations#token=...` previews
it; an explicit setup action starts account onboarding, and an explicit authenticated acceptance
is the only operation that grants membership. The frontend captures/removes the fragment before
initializing Keycloak; identity callbacks never contain that capability. A brief per-tab
sessionStorage handoff supports OIDC login, is cleared on initialization, and never stores bearer
tokens. Reopen the original invitation email after finishing the separate Keycloak actions email.

Acceptance requires the invited verified email and OIDC subject, live invitation state, and the
issuer's current authority for its current role. Issuer removal or downgrade therefore invalidates
unacceptable grants. A tenant-document write fences concurrent authority changes. Membership,
first Pole user, default selection, immutable acceptance receipt and membership outbox write
commit in one Mongo transaction. Concurrent first joins fence one subject's Users document, so
exactly one membership becomes default. Later acceptance opens that tenant without changing an
existing default. Removal preserves the identity and user; it repairs the default among remaining
memberships, or clears it after the last membership. The console then shows “No tenant access”.
A fresh acceptance restores a valid default. Used-link retries cannot restore removed access.

Invitation secrets contain 256 random bits. Mongo stores only a hash for lookup and an envelope-
protected delivery value bound to tenant, invitation and revision. Outbox messages contain IDs
and revision only. The delivery consumer rechecks current revision, expiry and issuer authority,
then uses SMTP outside the transaction. Delivery state and its notification commit atomically.
SMTP is at-least-once: an in-flight email can arrive after a revoke/resend or be duplicated after
a process failure, but the obsolete link cannot grant access. Failed delivery is visible and
an authorized resend generates a new revision. Retain envelope keys needed by pending deliveries.

## Self-host mail and identity configuration

Configure Pole with a trusted frontend origin; request Host/Origin headers never build links:

```json
{
    "Invitations": {
        "FrontendUrl": "https://console.example.com",
        "Smtp": {
            "Host": "smtp.example.com",
            "Port": 587,
            "EnableSsl": true,
            "From": "pole@example.com",
            "Username": "pole"
        }
    },
    "KeycloakAdmin": {
        "BaseUrl": "https://identity.example.com",
        "Realm": "pole.control-plane",
        "AdminClientId": "pole.control-plane.admin",
        "FrontendClientId": "pole.control-plane.ui"
    }
}
```

Provide `Invitations__Smtp__Password` and `KeycloakAdmin__AdminClientSecret` through your secret
store/environment. Configure SMTP separately on the Keycloak realm, with its own host, port,
sender, authentication and TLS settings. The development realm's `POLE_SMTP_HOST` and
`POLE_SMTP_PORT` placeholders are supplied by Aspire; production deployments must supply or
replace them with their real relay settings. Keycloak supports [environment placeholders during
realm import](https://www.keycloak.org/server/importExport). Keep public registration disabled
and allow the exact public frontend `/invitations` callback in the UI client's redirect URIs.
Configure Keycloak's public hostname and proxy settings so its emailed action links are reachable
by recipients. The admin client needs the existing manage-users service-account authority.

Aspire supplies the `pole-mail` Mailpit container to both Pole and Keycloak. Its `smtp` endpoint
receives mail; the `http` endpoint exposes the local inbox and JSON API. E2E instances use isolated
ports and container ownership. Mailpit is a development/test inbox, not a production relay.

## Verification

`TenantInvitationTransactionTests` proves Mongo acceptance, retries, concurrent first joins,
identity checks, live issuer authority and rollback. HTTP tests prove public capability boundaries,
route scoping and actual management authorization. Frontend tests cover role restrictions,
origin changes and capability handoff. The real Aspire browser journeys exercise Keycloak,
both SMTP senders, new/existing invitees, revocation during setup and zero-membership re-entry.
