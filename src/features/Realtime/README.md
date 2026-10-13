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

Platform catalog owners use `InvalidateRealtimeTenants` for public eligibility changes.
It discovers active tenants through an expiring topic index, then rechecks each recipient's
session and current tenant permission through normal delivery. These hints contain the tenant
only; platform IDs, mappings and credentials remain in the separately authorized HTTP APIs.
Topic indices are changed atomically with replacement, removal and revocation. During a rolling
upgrade, older subscriptions acquire the additive index on their next replacement; the metadata
safety refresh continues to cover that bounded transition.

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

## Coverage and diagnostics

Coverage is acknowledged per subscription; transport connectivity or an arriving hint never
proves coverage. Metadata topics use provider-owned committed events and projection adapters.
`runtime-observations` is tenant-scoped by physical deployment ID; WebServices, Redis and
PostgreSQL contribute their native change sources through the observation catalog. PostgreSQL
supports its selected native StatefulSet or CloudNativePG source. `storage-observations` is
platform-admin-only and targets one deployment target. A disabled, unavailable or unregistered
owner remains uncovered without disabling available sibling topics.

Clients coalesce hints for 250 ms, retain a running HTTP read and perform one trailing read.
Observation hints fence exact scoped read epochs and version floors before resync; a coalesced
heartbeat can recheck recovered coverage, but only its current acknowledgement enables it.
Healthy heartbeats renew interests without repeatedly refetching HTTP metadata. Hidden pages
release interests. See the [observation lifecycle](../ResourceDeployments/README.md#shared-observation-lifecycle)
and [frontend query ownership](../../api/frontend/README.md#realtime-query-ownership).

`Pole.Realtime` measures delivery attempts, authorized send recipients, bounded outcomes and
server authorization/send duration; it also records connection, subscription and coverage
transitions. These measurements do not establish commit-to-browser latency or successful
application connectivity. Metric labels contain registered topics, normalized scopes/outcomes
and a controlled server/client source, never account or workspace identifiers.

The optional hub method `ReportClientLifecycle` accepts only `{reconnects, fallbacks}`, each
0–100 and not both zero. It rechecks the current cookie ticket, subject, session revocation and
expiry without changing any subscription or workspace authority. Accepted reports are at least
ten seconds apart per connection; `false` means the client should retain its coalesced counts.
Reports are best-effort client observations, distinguishable by `source=client`; existing clients
need not invoke the method. Reports are not persisted, published through the outbox or stored in
the query cache. Diagnostic transport failure must leave ordinary actions usable.
