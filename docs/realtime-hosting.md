# Realtime hosting across API replicas

Realtime is opt-in through `Realtime:Enabled`. Disabling it leaves polling available and does
not register Redis connections, cookie tickets or the SignalR Redis backplane. Topic owners
register their own scope, read authorization and lifecycle adapters; enabling the transport
alone does not acknowledge coverage for an unregistered or disabled topic.

## Shared infrastructure

All replicas in one Pole installation must use the same dedicated `ConnectionStrings:realtime`,
Data Protection application name (`Pole`), persistent Redis keyring and private key-protection
certificate. This infrastructure Redis is separate from RedisServices workloads. Redis provides
both the SignalR backplane and the expiring session/subscription registry.

Configure Redis persistence and `maxmemory-policy noeviction`. The Data Protection keyring has no
TTL and must survive Redis/container restarts. Tickets, subscriptions and their indices expire;
losing them disconnects current coverage until the browser bootstraps a fresh session and resyncs
its HTTP queries. Backplane notifications are transient and are not replayed after an outage.
Run Redis near the API replicas and restrict access to the API identity. Use an authenticated TLS
connection string when Redis crosses an untrusted network.

The composition root calls `AddRealtimeFeature(configuration, development)` followed by
`AddRealtimeRedis(configuration, development)`, installs `UseRealtimeOriginGuard()` before
realtime endpoints, and calls `MapRealtimeEndpoints()`. Both registration methods return early
when disabled. The Redis adapter belongs to Realtime.Application; the host supplies configuration.

## Cookie and authorization boundary

The browser POSTs its bearer-authenticated bootstrap to `/api/v1/realtime/session`; subsequent
hub negotiation/transports use the host-only HttpOnly cookie. Production uses Secure and Lax,
requires a same-site reverse proxy and never puts JWT/refresh tokens in URLs. The bootstrap copies
only issuer, subject, IdP session, roles and an opaque realtime session ID. The distributed ticket
key is that ID; its value is a Data Protection protected framework ticket with no saved tokens.
Expiry is the earlier of bearer expiry and five minutes, and renewal is explicit rather than
sliding. A renewal cannot recreate a ticket removed by logout.

The connection authenticates an account rather than a selected tenant. Each subscription is
validated against its provider's registered scope and current read permission. Publisher delivery
loads the current ticket and rechecks permission. Tenant-topic indices use distinct keys for each
tenant. Atomic Lua updates maintain connection/topic/session indices together; logout removes all
session interests and fences concurrent late subscriptions for the maximum session lifetime.

`Realtime:AllowedOrigins` must contain exact HTTP(S) origins, with no path, trailing slash, query
or wildcard. Credentialed CORS and the origin guard protect cookie changes, negotiation and
WebSocket upgrades. A safe transport GET without Origin is accepted only with
`Sec-Fetch-Site: same-origin`; WebSocket upgrades and mutating requests always require an exact
allowed Origin. Proxies must forward Origin and upgrade headers correctly. Redis scale-out still
requires affinity for negotiated connection transports unless clients use WebSockets with skipped
negotiation; the browser may use fallback transports, so configure replica affinity at the proxy.
See Microsoft's [Redis backplane guidance](https://learn.microsoft.com/en-us/aspnet/core/signalr/redis-backplane).

## Production configuration

Provide `Realtime:KeyProtectionCertificatePath` to a PFX containing its private key and supply
`Realtime:KeyProtectionCertificatePassword` from the deployment's secret configuration when the
PFX is password protected. All replicas must read the same certificate. The adapter fails startup
when realtime is enabled without its Redis connection or encryption certificate. Redis XML key
material is encrypted with the certificate; the certificate and its password must be backed up
separately from Redis. Do not remove the old certificate while persisted keys still need it; rotate
with a controlled keyring/certificate migration. Microsoft documents the
[Data Protection key storage and encryption contract](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/implementation/key-storage-providers).

A shared Redis endpoint is an installation boundary: independent Pole installations must use
separate Redis databases or endpoints, because registry keys and backplane channels use stable
Pole names. Do not share a database across unrelated installations.

## Local Aspire

Set `Realtime:Enabled=true` in AppHost configuration or `Realtime__Enabled=true` in its environment.
The default AppHost configuration leaves it disabled. When enabled, Aspire creates a dedicated
`realtime` Redis resource, enables AOF persistence and no eviction, supplies its connection string
to the API and waits for Redis. Its allowed frontend Origin comes from the actual Vite endpoint,
including the dynamically assigned E2E port. E2E Redis keeps persistence within its session
container and uses no shared named volume, so isolated runs do not reuse another run's keyring.

For local runs without an explicit certificate path, Aspire generates
`.artifacts/realtime/key-protection.pfx`, restricts its file permissions to the current user on
Unix, and reuses it across restarts. This ignored local certificate is for development only. If
using a password-protected certificate through Aspire, set its path and the secret parameter
`Parameters:realtime-key-protection-certificate-password`; Aspire passes the password as a secret
parameter. Publish mode requires an explicit certificate path.

## Failure and validation

Redis commands use fail-fast backlog behavior. Redis unavailability prevents ticket reads and
new subscription coverage instead of accepting an unverified local session. The browser restores
polling when its realtime connection is unhealthy, and after reconnect it waits for subscription
acknowledgement then refetches active HTTP queries. Outages can lose transient invalidations, so
HTTP/polling safety reads remain necessary. External IdP logout has no backchannel in this stage;
an established session expires within five minutes, and explicit Pole logout revokes it immediately.
An already issued valid bearer can bootstrap another session until that bearer expires. A hard
five-minute bound on external IdP logout therefore requires token introspection or a backchannel;
cookie expiry alone does not provide it.

`RealtimeRedisTests` uses a disposable persistent Redis plus two actual Kestrel servers and
SignalR WebSocket clients. It proves shared encrypted tickets and keyrings, tenant index isolation,
atomic replacement/removal, revocation fencing, cross-replica delivery, API restart, Redis outage
failure and recovery. `RedisInfrastructureRegistrationTests` proves disabled composition and
required connection/certificate configuration. These focused checks require a Docker-compatible
runtime; broader Aspire/browser/Keycloak rollout verification remains in the final realtime gate.
