# Users and identity

Users owns Pole user records and the Keycloak Admin API integration. Explicit platform-admin
`POST /api/v1/users` remains an administrative provisioning flow and creates a personal default
tenant through the UserCreated event. Tenant invitations never call that flow or emit that event.

Invited identities are created only after a recipient explicitly starts onboarding. Public
Keycloak registration stays disabled. The provider looks up existing identities by normalized
email. Existing verified identities use normal login; unverified identities receive VERIFY_EMAIL
without a password reset. Newly invitation-provisioned identities receive VERIFY_EMAIL and
UPDATE_PASSWORD through Keycloak's actions email. An administrator-only managed Keycloak profile attribute (`pole-invitation-setup`) and unfinished
required actions keep a failed initial setup email recoverable; established credentials are
never reset by invitation setup. The actions callback is a trusted frontend URL without an
invitation capability. It instructs recipients to reopen the original Pole invitation.

`Users.Contracts` publishes narrow caller-session operations for an invitation acceptance to
create/fence a user without a personal tenant and for removal to reconcile the default tenant.
The subject and normalized email are unique identities. Membership/default/receipt/outbox changes
share the caller's Mongo transaction; removing all memberships preserves the user and identity.
See [tenant invitations](../Tenants/README.md) for SMTP, public callback and self-host configuration.

When using an existing/custom realm, configure `pole-invitation-setup` as a managed user-profile attribute with `view: ["admin"]` and `edit: ["admin"]`, optionally limited to the value `true`. Preserve the standard profile fields and keep this marker hidden and non-editable for users. The bundled realm includes this policy; unmanaged custom attributes are insufficient for reliable retry recovery.
