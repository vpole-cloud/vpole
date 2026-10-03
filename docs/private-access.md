# Private Access operations

Private Access forwards a local database client's TCP connection to a provider-owned Kubernetes
Service. Pole owns the authorization, destination, and gateway lifecycle; the user supplies
database credentials to their database client separately. The [feature README](../src/features/PrivateAccess/README.md)
documents the API and lease rules, and the [CLI README](../src/tools/Pole.Cli/README.md) documents
the commands.

## Request flow

1. A tenant Owner or Admin grants a current tenant member access to an exact resource deployment
   and provider endpoint. The grant contains no destination address or database credential.
2. Pole reconciles one Access Gateway Deployment per `(tenant, deployment target)` that has an
   active, applicable grant. It uses the target's existing Kubernetes credentials to create a
   separate restricted namespace, identity Secret, default-deny policy, exact destination rules,
   and one-replica Deployment. The gateway pod has no Kubernetes API credentials.
3. The gateway opens an outbound mTLS control WebSocket to Pole Server. Pole verifies the exact
   certificate pin against its current tenant-and-target registration. The control connection
   must renew its short lease; a revoked or expired identity loses its connection. One Server
   replica atomically owns each `(tenant, deployment target)` in MongoDB. Its lease epoch fences
   old owners after a takeover.
4. `pole forward postgres/orders` uses the active tenant and an authorized grant. The CLI binds
   `127.0.0.1` only. Every accepted TCP connection receives a separate short-lived one-use
   session ticket and opens an HTTPS WebSocket to the server. The ticket is sent in the Bearer
   header, never in the URL.
5. Any Server replica can accept the CLI or gateway WebSocket. A replica that does not own the
   target forwards it to the owner over a private, mutually authenticated HTTPS channel. The
   gateway certificate is checked at ingress and its current pin is checked again at the owner.
   Only the owner redeems a CLI ticket or claims a gateway rendezvous ticket. Pole rechecks
   membership, grant, placement, target isolation, and provider endpoint before authorizing a
   stream. It gives the gateway a one-use rendezvous ticket. The gateway opens a
   separate outbound mTLS WebSocket and dials only the server-supplied Service and port after a
   fresh lease succeeds. Pole relays bounded TCP frames, including half-close, between the CLI
   and gateway. It does not create a pod or Job per TCP connection.
6. Revocation, changed placement, lost gateway control, or lease expiry closes affected streams.
   A gateway restart drops existing TCP sessions; the CLI can establish new sessions after the
   gateway reconnects. Pole retries gateway reconciliation and ownership-checked cleanup after
   interruption.

The gateway registration holds a durable reference to its deployment target. Pole creates the
registration before provisioning Kubernetes resources and releases the reference only after
owned Gateway resources have been removed. Disabling the target or losing NetworkPolicy
attestation withdraws the Gateway; cleanup may still use the target's stored Kubernetes
credentials. The dedicated namespace and its default-deny policy remain after cleanup. They
contain no running workload or credential and are retained to avoid deleting unrelated objects
that an operator may have placed in that namespace.

A platform administrator can inspect one tenant and target with
`GET /api/v1/platform/private-access/gateways/{tenantId}/{deploymentTargetId}`. The route
does not need `X-Pole-Tenant-ID`. `Required` reflects applicable active grants and applied
placements. Availability identifies disabled configuration or target, missing NetworkPolicy
attestation, provisioning, waiting for the first connection, disconnection, expired identity,
removal, or connection. `Connected` requires a matching live broker-owner lease and a gateway
contact in the last 20 seconds, so this is a point-in-time observation, not a probe of the
Kubernetes pod. The response includes contact time, credential generation and expiry, and the
gateway control protocol version. `gateway-v1` reports the `tcp-forward` capability; this is
protocol metadata, not the image version. It does not expose certificate pins, private broker
origins, destination addresses, or credentials.

Control sockets and rendezvous tickets stay in the owning Server process. MongoDB stores only
the short owner lease, epoch and private origin, never socket or ticket state. Claim, renewal and
expiry checks use MongoDB server time, so replica clock drift cannot advance a takeover. The
epoch remains monotonic even after a gateway is removed and recreated. Different targets
may have different owners. No sticky public routing is required. If an owner crashes, its lease
expires within ten seconds; the gateway reconnects and a new owner claims the target. Existing
TCP sessions on the failed process end and the CLI may open new ones. A planned Server shutdown
rejects new sessions, allows existing ones a 20-second drain window, then closes them. The
`MaximumSessions` limit applies per owner replica across direct and forwarded sessions.

## Configuration

Private Access is disabled by default. When enabled, configure these server settings and expose
the gateway listener to the target cluster:

| Setting | Example | Purpose |
| --- | --- | --- |
| `PrivateAccess__Enabled` | `true` | Enables session transport and gateway reconciliation. |
| `PrivateAccess__GatewayPort` | `9444` | Dedicated mTLS gateway listener port. |
| `PrivateAccess__GatewayImage` | `registry.example/pole-access-gateway:VERSION` | Explicit versioned image; `:latest` is rejected. |
| `PrivateAccess__BrokerUri` | `https://pole.example/` | Public HTTPS server origin. The gateway listener uses this host and `GatewayPort`. |
| `PrivateAccess__BrokerAddresses__0` | `203.0.113.10` | Exact reachable IP for gateway egress policy; add indexed values for all addresses. |
| `PrivateAccess__MaximumSessions` | `64` | Concurrent user TCP session limit. |
| `PrivateAccess__MaximumGatewayControls` | `1024` | Concurrent direct or forwarded gateway control connections per Server replica. |
| `PrivateAccess__InstanceId` | `pole-server-0` | Unique identity for this Server process; do not reuse concurrently. |
| `PrivateAccess__InternalPort` | `9445` | Dedicated HTTPS listener for Server-to-Server forwarding. |
| `PrivateAccess__InternalOrigin` | `https://pole-server-0.pole-server-internal:9445` | Stable private origin advertised by this replica. |
| `PrivateAccess__InternalPeerOrigins__0` | `https://pole-server-0.pole-server-internal:9445` | Exact allowed peer origin; include every replica, including this one, on all replicas. |
| `PrivateAccess__InternalCertificatePath` | `/run/secrets/pole-server-peer.pfx` | Shared Server peer certificate with client and server TLS use. |
| `PrivateAccess__InternalCertificatePassword` | secret reference | Password for the peer PKCS#12 file. |
| `PrivateAccess__InternalCertificateSha256Pin` | 64 lowercase hex characters | Exact SHA-256 pin for inbound peer clients and outbound peer servers. |
| `Kestrel__Endpoints__PrivateAccessGateway__Url` | `https://0.0.0.0:9444` | mTLS listener URL; port must equal `GatewayPort`. |
| `Kestrel__Endpoints__PrivateAccessInternal__Url` | `https://0.0.0.0:9445` | Private peer listener URL; port must equal `InternalPort`. |
| `Kestrel__Endpoints__PrivateAccessGateway__Certificate__Path` | `/run/secrets/pole-gateway-listener.pfx` | Server TLS certificate. |
| `Kestrel__Endpoints__PrivateAccessGateway__Certificate__Password` | secret reference | Certificate password from the deployment secret mechanism. |
| `Cli__Issuer` | `https://identity.example/realms/pole.control-plane` | Public OIDC issuer returned by `/.well-known/pole-cli`. |

Build the gateway image from the repository root so the build sees the pinned SDK and central
package versions:

```sh
docker build -f src/tools/Pole.AccessGateway/Dockerfile \
  -t registry.example/pole-access-gateway:VERSION .
```

Pole supplies the image, broker origin, tenant/target identity and mounted PKCS#12 certificate
to the gateway Deployment. Operators do not run an enrollment command or copy a token. The
gateway listener's TLS certificate must match the configured broker hostname and be trusted by
the gateway image. Its port and each configured broker IP must be reachable from the target.
The internal listener must be reachable only by Pole Server replicas. Give each replica a stable
private hostname and list every replica's exact origin in `InternalPeerOrigins`; membership
changes require updating that list on all replicas. Mount the same dedicated peer certificate
on each replica, keep its private key in the deployment secret mechanism, and rotate the pin and
certificate together. Do not expose the peer listener through the public ingress or gateway
Service. The Server validates the peer certificate pin at both ends and refuses owner origins
outside the configured list.
The exact-IP egress policy uses `/32` IPv4 or `/128` IPv6 entries; update these when the broker
address changes. The target must attest NetworkPolicy enforcement, and the destination workload
must have the matching runtime-cell isolation baseline. An isolation override alone does not
qualify.

Pole uses the deployment target's Kubernetes API credentials for provisioning. Gateway pods run
as non-root, with a read-only root filesystem, no mounted ServiceAccount token, and no cluster
RBAC. Secrets contain gateway identity only; database credentials are never put in grants,
session tickets, gateway environment variables or gateway Secrets.

## Client setup

Register public OIDC client `pole.control-plane.cli` with Authorization Code + PKCE S256 and the
exact callback `http://127.0.0.1:48763/callback`. Do not configure a client secret. Then:

```sh
pole login --server https://pole.example
pole tenant select tenant-1
pole forward postgres/orders --local-port 15432
```

Connect the database client to `127.0.0.1:15432` and provide its database credentials through
its normal secure credential mechanism. `pole status`, `pole tenant list`, and `pole logout`
manage the local profile. The CLI obtains the issuer and client ID from the selected Pole Server
and can silently refresh during forwarding; an expired login requires `pole login` again.
