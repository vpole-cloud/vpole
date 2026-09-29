# Private Access TCP broker prototype

This executable experiment tests a narrow TCP broker before integrating issues #47–49 into
Pole. It is not registered in Pole.Server and does not implement the production Private Access API.
No backward compatibility or migration is required.

## Ownership and provider boundary

`PrivateAccess.Core` owns the identity, exact grant/deployment/endpoint binding, authorization
outcomes, admission coordinator and `IPrivateAccessTransport`. It has no dependency on TCP,
WebSocket, Kubernetes, database providers or OpenZiti. A future OpenZiti adapter would implement
this boundary and must pass the same behavioral requirements; implementing the interface alone
does not establish equivalent revocation or partition guarantees.

`PrivateAccess.Tcp` implements the first transport. `PrivateAccess.Tool` provides three executable
roles and a disposable fixture authority. The test project is in `Pole.slnx`, so the normal full
quality gate discovers it.

## Flow

1. A connector opens an outbound TLS 1.3 connection to the broker. The broker maps its pinned
   client certificate to exactly one target. There is no inbound connector listener.
2. A local forward binds only `127.0.0.1`. Each local TCP connection gets a separate TLS connection
   and a fresh online authorization for its exact grant, deployment and endpoint. The prototype
   maps the client's pinned certificate to a tenant and subject.
3. The broker selects a connector for the target returned by the authority. The connector resolves
   `(tenant, deployment, endpoint)` through its own catalog. A client cannot supply a host or port;
   unknown JSON fields are rejected.
4. Before dialing the destination, the connector requests a fresh lease. During the stream it
   renews periodically. Each renewal rechecks the same identity, request and grant revision.
5. Revocation, expiry, unavailable authority or a changed binding ends the stream. Each connector
   enforces its deadline locally even if the broker stops replying while data still flows.
6. A disconnect creates a new connector connection and fresh authorization. Existing streams do
   not resume transparently. TCP half-close permits a backend to return a final response after
   the client stops writing.

The broker proxies encrypted bytes but does not interpret database protocols or issue database
credentials. TLS terminates at the broker and connector; the database leg uses whatever protocol
TLS the database client itself negotiates. This is not broker-blind end-to-end encryption.

## Lease rule

The tool uses a 3-second maximum lease; the implementation caps configuration at 10 seconds.
A connector sends a random challenge and records a monotonic timestamp. A reply echoes the nonce
and supplies a bounded duration. The deadline is **send timestamp + duration**, never receive
timestamp + duration. Late, replayed, overlong and already-expired replies are rejected. Loss of
renewals therefore cannot leave a data stream running indefinitely. Expiry and cancellation are
subject to normal OS/process scheduling; they are not a hard realtime guarantee.

The broker also times out a pending authorization even if the authority ignores cancellation.
Fresh admissions never fall back to cached permissions. A shorter grant expiry shortens the lease.

Frames are bounded to 16 KiB with a four-frame receive queue. TLS handshakes, client sessions and
connector sessions have separate capacity limits. One connector connection carries one session;
the tool runs four outbound workers. Idle registrations expire after 30 seconds and reconnect.
Backpressure can delay control frames and cause fail-closed disconnects; high-throughput fairness,
keepalive tuning and multiplexing are deliberately not established by this experiment.

## Run locally

Use the SDK pinned by `global.json`, from the repository root:

```bash
dotnet build src/examples/PrivateAccess/PrivateAccess.Tool/PrivateAccess.Tool.csproj
dotnet run --no-build --project src/examples/PrivateAccess/PrivateAccess.Tool -- init /tmp/pole-private-access-demo
```

The fixture directory must not exist. It contains one-day self-signed certificates and
`fixture.json`. Private keys are owner-readable on Unix. Each role loads only its own private key;
the other certificates are public pins. Keep this disposable directory private and remove it
when finished. These are local test credentials, not enrollment artifacts.

Configure `DestinationHost`/`DestinationPort` in `fixture.json` for a local test database.
The defaults target Redis at `127.0.0.1:6379`, broker at `127.0.0.1:14443`, and local forward at
`127.0.0.1:16379`. Start each role in a separate terminal:

```bash
dotnet run --no-build --project src/examples/PrivateAccess/PrivateAccess.Tool -- broker /tmp/pole-private-access-demo
dotnet run --no-build --project src/examples/PrivateAccess/PrivateAccess.Tool -- connector /tmp/pole-private-access-demo
dotnet run --no-build --project src/examples/PrivateAccess/PrivateAccess.Tool -- forward /tmp/pole-private-access-demo
```

Connect your database client to the local forward. The broker rereads `fixture.json` on every
admission and renewal. Set `Enabled` to `false`, move `ExpiresAt` into the past, change the grant
revision, or temporarily rename the file to exercise revoke, expiry and authority failure.
Publish changes through an atomic file replacement: a partially written file fails closed too.
Use Ctrl+C to stop each role.

## Verification and remaining work

```bash
dotnet test src/examples/PrivateAccess/PrivateAccess.Tests/PrivateAccess.Tests.csproj
dotnet test src/examples/PrivateAccess/PrivateAccess.Tests.Integration/PrivateAccess.Tests.Integration.csproj
```

Tests use real loopback TCP and TLS sockets. They cover bidirectional forwarding, half-close,
identity and endpoint mismatches, target impersonation, revoke, expiry, authority loss/hang,
revision changes, reconnect, destination injection, bounded framing and nonce/deadline rules.
A separate partition test keeps data flowing while a simulated broker withholds renewals and
checks that the connector closes the real backend socket independently.

The separate infrastructure suite uses real Npgsql and StackExchange.Redis clients through the
local forward into disposable PostgreSQL 18 and Redis 8.2 containers. It covers transactions,
128 KiB binary payloads, RESP2/RESP3 Pub/Sub, blocking commands, sustained and idle connections,
concurrent revoke/expiry and broker/connector/database restarts. See [validation](VALIDATION.md)
for scope, reproduction and limits. Every new physical database connection requires authorization;
client-side pooling is disabled in the PostgreSQL tests.

This is transport evidence, not completion of #47–49. Still required before production integration:

- Keycloak/OIDC CLI login and session credentials; this experiment uses pinned user certificates.
- Tenant-scoped grant CRUD, roles, Mongo persistence, server-owned endpoint discovery and metadata audit.
- Single-use connector enrollment, credential rotation/revocation and operational diagnostics.
- Per-deployment data-plane identity, Kubernetes RBAC and NetworkPolicy isolation. The local
  endpoint catalog is an allowlist, not a network sandbox against a compromised connector.
- Redis/PostgreSQL deployment integration, production-scale load/long-duration testing and broader
  failure injection. Local container tests establish protocol correctness, not deployment readiness.
- An OpenZiti implementation and the same provider acceptance suite; no second adapter is included.

Do not expose this prototype to untrusted networks. No PR publication is justified by focused
prototype tests alone: the repository's complete local quality gate remains mandatory.

Full publication evidence is recorded by `scripts/quality_gate.py` for the exact source tree.
Focused transport checks do not replace the complete backend, frontend and infrastructure gate.
