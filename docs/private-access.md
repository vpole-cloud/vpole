# Private Access operations

This guide describes the current Private Access implementation and its deployment requirements. The feature-owned [Private Access README](../src/features/PrivateAccess/README.md) is the canonical API and lifecycle reference; the [CLI README](../src/tools/Pole.Cli/README.md) covers native-client commands.

## Request flow

1. A platform administrator enrolls a connector for an enabled deployment target. An authorized user creates a tenant-scoped grant for a current tenant member, exact resource deployment, and provider-owned endpoint. The grant contains no destination address or database credential.
2. The grant subject signs in through the public Keycloak client `pole.control-plane.cli` using Authorization Code + PKCE S256. The CLI uses its fixed callback `http://127.0.0.1:48763/callback`, checks state, and stores refreshable tokens in the current user's restricted data directory.
3. `pole forward` lists the caller's grants using `GET /api/v1/private-access/grants` with `X-Pole-Tenant-ID`, then binds the selected local port to IPv4 loopback only. The user chooses a tenant and grant; that grant fixes the deployment and endpoint.
4. Each accepted local TCP connection requests a fresh session from `POST /api/v1/private-access/grants/{id}/sessions`. The CLI presents the one-use ticket as a Bearer authorization header to the Pole server's HTTPS WebSocket `/private-access/streams`. Tickets are not put in URLs or logs. The API consumes a ticket once and rechecks the grant, tenant membership, exact deployment, target, endpoint, and isolation admission.
5. The broker creates a short-lived worker Job in a dedicated namespace, installs a persistent namespace default-deny NetworkPolicy plus session-scoped allow policies before the pod can run, and mounts a one-use rendezvous ticket from a Kubernetes Secret. The worker connects to `/private-access/workers`, proves fresh lease challenges, and only dials the fixed Kubernetes service and port after the broker approves setup. The broker relays bounded binary TCP chunks and preserves TCP half-close.
6. The worker can egress only to the exact target endpoint and configured broker IPs, plus cluster DNS. Its destination-side ingress policy admits only that session's worker. Lease expiration, revocation, changed membership or placement, connector loss, and cancellation terminate the stream and start ownership-checked cleanup. The cleanup service retains a target reference until worker deletion is confirmed.

The control-plane API route prefix is `/api/v1`. Grant management routes and authorization are documented in the feature README. This transport does not provide database credentials: for Pole-managed PostgreSQL, the existing tenant-authorized `GET /api/v1/postgres/{clusterId}/connection-string` returns a credential-bearing connection string. Treat that response as a secret and configure database clients separately from the tunnel.

## Connector enrollment and listener

Enrollment uses the ordinary Pole HTTPS API. A platform administrator issues a short-lived enrollment using `POST /api/v1/platform/deployment-targets/{targetId}/connectors/enrollments` with the target's current revision. The response contains a one-time token; save only that token to a protected file for the `--token-file` option. The connector creates a self-signed client certificate and private key locally, sends the certificate and token to the anonymous redemption route `POST /api/v1/platform/deployment-targets/{targetId}/connectors/enroll`, and promotes the certificate to its active identity after acceptance. The token expires after 15 minutes and its digest is stored server-side.

The connector status API reports `Unknown` before first contact, the observed health while active and seen within 10 seconds, and `Disconnected` after that window or credential revocation/expiry. `LastContactAtUtc` remains visible.

Enrollment and connector traffic use different listeners. Enrollment is an authenticated administrative operation on the normal API. The long-lived connector then makes an outbound mutual-TLS WebSocket connection to `wss://<broker-host>:9444/private-access/connectors` by default. Port 9444 requires a client certificate; the server maps its SHA-256 fingerprint to the persisted connector and target identity. Do not expose the connector listener as an unauthenticated HTTP route or use it for enrollment.

Build the shared image from the repository root so the build includes `global.json`, central package versions, and shared compiler settings. It has both `enroll`/`connector` commands for the external connector and the `worker` command used by session Jobs:

```sh
docker build -f src/tools/Pole.Connector/Dockerfile \
  -t registry.example/pole-connector:VERSION \
  .
```

Assuming the enrollment token has been written to a protected host file, create a mode-0700 state directory owned by the identity that will run the image, then enroll and run the connector with the same persistent mount:

```sh
mkdir -m 700 pole-connector-state
chmod 600 pole-connector-enrollment-token

docker run --rm --user "$(id -u):$(id -g)" \
  -v "$PWD/pole-connector-state:/var/lib/pole-connector" \
  -v "$PWD/pole-connector-enrollment-token:/run/secrets/enrollment-token:ro" \
  registry.example/pole-connector:VERSION enroll \
  --origin https://pole.example \
  --target <24-character-target-id> \
  --token-file /run/secrets/enrollment-token \
  --state-dir /var/lib/pole-connector

docker run --rm --user "$(id -u):$(id -g)" \
  -v "$PWD/pole-connector-state:/var/lib/pole-connector" \
  registry.example/pole-connector:VERSION connector \
  --origin https://pole.example:9444 \
  --state-dir /var/lib/pole-connector
```

The state directory holds the connector PKCS#12 identities and must be writable by the process user. The current store supports Linux and macOS Unix file modes; it explicitly does not support Windows. Run the connector with a persistent state volume so a restart does not lose its enrolled identity. The connector process makes outbound connections and has no Kubernetes API client, ServiceAccount token, or cluster RBAC requirement.

## Broker configuration

Private Access is disabled by default. `PrivateAccess:Enabled` must be set to `true` to enable the endpoints and worker provisioning. When enabled, configure the broker options and a named Kestrel endpoint. For environment-based deployment configuration, the expected names are:

| Setting | Example | Requirement |
| --- | --- | --- |
| `PrivateAccess__Enabled` | `true` | Explicit opt-in; defaults to false. |
| `PrivateAccess__ConnectorPort` | `9444` | Must match the connector Kestrel endpoint port. |
| `PrivateAccess__MaximumSessions` | `64` | 1 through 1024 concurrent user sessions. |
| `PrivateAccess__WorkerNamespace` | `pole-private-access` | Dedicated DNS-label namespace, different from workload namespaces. |
| `PrivateAccess__WorkerImage` | `registry.example/pole-connector:VERSION` | Explicit versioned image; `:latest` is rejected. This same image runs the `worker` command. |
| `PrivateAccess__BrokerUri` | `https://pole.example/` | HTTPS origin used by workers. Must have no path, query, or fragment. |
| `PrivateAccess__BrokerAddresses__0` | `203.0.113.10` | Exact reachable broker IP; add one indexed value for each address. Wildcards, loopback, and CIDRs are rejected. |
| `Kestrel__Endpoints__PrivateAccessConnector__Url` | `https://0.0.0.0:9444` | HTTPS listener URL; port must match `ConnectorPort`. |
| `Kestrel__Endpoints__PrivateAccessConnector__Certificate__Path` | `/run/secrets/pole-connector-listener.pfx` | Server TLS certificate presented on the connector listener. |
| `Kestrel__Endpoints__PrivateAccessConnector__Certificate__Password` | secret reference | Protect this value through the deployment's secret mechanism. |

Kestrel requires a client certificate on the connector listener. The server checks the certificate time window during TLS and then authorizes the exact fingerprint against the enrolled connector record. The HTTPS certificates used for normal API traffic and the connector listener must have DNS names matching their configured origins and a chain trusted by their clients. The CLI, connector, and worker use normal platform TLS validation; they do not have an insecure mode or a custom certificate bypass. For a private CA, install that CA in the relevant host/container trust stores (including the configured worker image) and verify the broker hostname matches the certificate SAN.

`BrokerAddresses` is used to create exact `/32` IPv4 or `/128` IPv6 worker egress rules to the broker port in `BrokerUri`. Keep these IPs current and reachable from the worker namespace. Do not replace them with a broad CIDR. Worker service DNS additionally requires cluster DNS egress. The target must attest that its CNI enforces NetworkPolicy, and the destination endpoint's ingress must admit the generated per-session policy. A target isolation override alone does not enable Private Access.

The server ensures the worker namespace has a persistent empty-ingress/empty-egress default-deny baseline before creating session rules. Cleanup removes only ownership-checked per-session policies; it does not remove the namespace baseline, so deletion or termination of an allow rule cannot restore unrestricted worker egress.

Cleanup recovery also runs when `PrivateAccess:Enabled` is false. It uses each session's persisted worker namespace, so changing the configured namespace or restarting the broker does not abandon old resources. A failed target cleanup retains its reference and is retried without preventing cleanup of other targets.

The normal Pole server must already have its configured deployment-target Kubernetes credentials because it creates and removes worker Jobs, Secrets, namespaces, and NetworkPolicies through its existing target client provider. The connector itself needs no cluster credential. Worker pods run without a mounted ServiceAccount token, as non-root, with a read-only root filesystem and dropped Linux capabilities.

## Client usage and credential boundaries

Sign in and start a forwarder with the native CLI:

```sh
dotnet run --project src/tools/Pole.Cli -- login \
  --server https://pole.example \
  --issuer https://identity.example/realms/pole.control-plane \
  --client-id pole.control-plane.cli

dotnet run --project src/tools/Pole.Cli -- forward \
  --server https://pole.example \
  --issuer https://identity.example/realms/pole.control-plane \
  --client-id pole.control-plane.cli \
  --tenant <tenant-id> \
  --grant <grant-id> \
  --port 15432
```

The Keycloak realm must have the public `pole.control-plane.cli` client with the registered fixed callback `http://127.0.0.1:48763/callback` and S256 PKCE. Do not add a client secret to this native client. The CLI binds only `127.0.0.1`; it displays the grant's server-owned resource, deployment, and endpoint metadata before listening. Use the normal API origin for `--server`; forwarding requires HTTPS even when loopback HTTP is allowed for local OIDC development.

For example, after a grant has been made and the forwarder is listening on `127.0.0.1:15432`, configure a local PostgreSQL client to connect there and provide database credentials through the client's normal secure credential mechanism. Pole does not put a database username/password into a Private Access ticket, connector certificate, worker environment, or CLI option. Avoid placing database credentials, enrollment tokens, OIDC tokens, or connector private keys in command-line arguments or logs.

## Current deployment limitations

- Connector presence and worker rendezvous are held in process memory. Until broker routing/state is shared, run one broker instance, or ensure the API session request, user WebSocket, connector WebSocket, and worker WebSocket for each target/session are all routed to the same instance. Ordinary load balancing across broker replicas can report no connector or fail rendezvous.
- The current chart does not expose the Private Access settings or connector listener. Configure the server environment, TLS secret, service port, and network policy through the deployment's existing customization mechanism.
- The connector identity store supports Unix hosts only. Windows connector deployment is unsupported.
- NetworkPolicy behavior depends on the target CNI and its attestation. Validate the exact worker-to-broker IP path and policy enforcement in each target before issuing grants.
