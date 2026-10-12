# Realtime notifications

Realtime is a platform capability for authenticated, scoped change hints. HTTP APIs
remain authoritative; browser query caches and form drafts keep their existing ownership.

`Realtime.Contracts` contains the provider extension contracts. Providers register a
`RealtimeTopic` and translate their own committed events through `PublishRealtimeChange`.
Observation providers can implement `IRealtimeTopicLifecycle` without depending on the
transport Application project. Duplicate topics and unknown enabled topics are rejected when
realtime is enabled. Disabled rollback ignores stale topic configuration. A subscription to an
unknown or disabled topic acknowledges `covered:false` without creating provider interests;
available siblings keep their coverage.

`Realtime.Application` owns the cookie session, hub, topic authorization and delivery.
`POST /api/v1/realtime/session` uses the existing Bearer scheme; the hub and session
deletion use the dedicated cookie scheme. Handshakes do not consume the active tenant
header. Subscriptions and delivery separately verify current tenant membership, with
the existing platform-admin bypass requiring an existing tenant.

User topics are independent of tenant membership: their tenant is absent and their resource ID
must exactly match the authenticated subject, including for platform administrators. `tenant-access`
uses this scope to refresh the caller's membership discovery after its own committed changes.
It carries no roles, other users or tenant lists. Platform access revocation retains user and
tenant subscriptions; malformed scopes cannot revoke unrelated permissions.

Sessions are bounded by both the JWT expiry and five minutes. Tickets retain only
issuer, subject, optional identity-provider session ID, role claims and an opaque
session ID. JWTs, refresh tokens, credentials, resource DTOs and log output are not
notification payloads. Cookie credentials are HttpOnly session cookies; they are not
written to JavaScript-readable local/session storage or query/mutation caches.

The feature is opt-in through `Realtime:Enabled`. Disabled registration requires no
distributed stores. Production transport adapters and hosting compose separately;
the capability has no process-local production ticket or subscription store.

Production and multi-replica configuration is described in
[realtime hosting](../../../docs/realtime-hosting.md).

The feature-local tests exercise route/scheme separation, expiry, origin restrictions,
tenant isolation, delivery-time membership, disabled startup and failure-safe provider
interest cleanup. Their authentication fixture proves routing, not real Keycloak JWT
validation; real Redis and browser flows are verified by integration/E2E tests.
