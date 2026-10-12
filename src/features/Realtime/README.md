# Realtime notifications

Realtime is a platform capability for authenticated, scoped change hints. HTTP APIs
remain authoritative; browser query caches and form drafts keep their existing ownership.

`Realtime.Contracts` contains the provider extension contracts. Providers register a
`RealtimeTopic` and translate their own committed events through `PublishRealtimeChange`.
Observation providers can implement `IRealtimeTopicLifecycle` without depending on the
transport Application project. Duplicate topics and unknown enabled topics are rejected.

`Realtime.Application` owns the cookie session, hub, topic authorization and delivery.
`POST /api/v1/realtime/session` uses the existing Bearer scheme; the hub and session
deletion use the dedicated cookie scheme. Handshakes do not consume the active tenant
header. Subscriptions and delivery separately verify current tenant membership, with
the existing platform-admin bypass requiring an existing tenant.

Sessions are bounded by both the JWT expiry and five minutes. Tickets retain only
issuer, subject, optional identity-provider session ID, role claims and an opaque
session ID. JWTs, refresh tokens, credentials, resource DTOs and log output are not
notification payloads. Cookie credentials are HttpOnly session cookies; they are not
written to JavaScript-readable local/session storage or query/mutation caches.

The feature is opt-in through `Realtime:Enabled`. Disabled registration requires no
distributed stores. Production transport adapters and hosting compose separately;
the capability has no process-local production ticket or subscription store.

The feature-local tests exercise route/scheme separation, expiry, origin restrictions,
tenant isolation, delivery-time membership, disabled startup and failure-safe provider
interest cleanup. Their authentication fixture proves routing, not real Keycloak JWT
validation; real Redis and browser flows are verified by integration/E2E tests.
