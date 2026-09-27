# Resource connections

ResourceConnections owns directed network access from one tenant resource to a published
endpoint of another resource. Runtime-cell membership does not grant connectivity: a connection
adds a narrow Kubernetes ingress allowance over the runtime cell's default-deny baseline.

## Scope and identity

A connection has an immutable `Name`, source resource, destination resource, and endpoint ID.
Names are unique within the tenant and this resource kind. References use stable resource IDs;
deleting and recreating a resource with the same name never reconnects an old declaration.
This preserves the future IaC address model of `kind/name` without implementing an IaC engine.

Sources can be WebServices, Redis services, PostgreSQL clusters, or Function Apps. Functions
are source-only. A destination publishes the following endpoint catalog:

| Destination | Endpoint ID | Service port | Pod target port |
| --- | --- | --- | --- |
| Redis | `redis` | 6379 | 6379 |
| PostgreSQL, either provider | `postgres` | 5432, read-write service | 5432 |
| WebService | Normalized named TCP port | Configured service port | Configured numeric target port |

WebService endpoint names are trimmed and lowercased. Unnamed, ambiguous, non-TCP, and unresolved
named-target entries are excluded. The catalog reads the desired resource configuration in the
caller's Mongo transaction; it does not depend on a previous successful Kubernetes apply.

The API accepts resource and endpoint identities. Kubernetes selectors, namespaces, raw addresses,
and arbitrary ports remain server-owned. A connection does not distribute credentials, create
SecretBindings, inject application environment variables, expose public ingress, or restrict egress.
The client uses the returned service host and service port and supplies its own authentication.

## Placement and tenant boundary

Both resources must belong to the active tenant. Cross-resource-group and cross-isolation-scope
connections are supported. Every desired source deployment must have exactly one desired
destination deployment on the same deployment target. At least one source deployment is required.
A missing or ambiguous destination rejects the whole declaration before any policy is written.
No cross-cluster networking is synthesized.

Creation fences both resource registrations in deterministic order, resolves placements and the
provider endpoint in one Mongo transaction, and inserts the connection with its outbox event.
Placement and endpoint writers acquire the same registration fence. After their tentative writes,
they validate all affected connections in the same transaction:

- Removing a destination needed by a remaining source rejects and rolls back the operation.
- Removing a source target can succeed when the remaining topology is valid; old policy locations
  remain as cleanup snapshots, including after cleanup succeeds, so later writes at an old
  location can be found and removed.
- Removing or renaming a referenced WebService endpoint rejects the update.
- Changing ports under the same endpoint ID refreshes connection snapshots and requests a new
  policy generation.

This registration write closes the race between a new connection and a placement mutation even
when the connection did not exist when the other transaction began. Namespace, workload labels,
and actual placements are resolved from provider-owned contracts rather than client input.

## API

All routes are under `/api/v1/resource-connections` and require the active tenant header
`X-Pole-Tenant-ID` and normal bearer authentication. Owner, Admin, and Member can create or delete;
Viewer can read. A foreign-tenant connection is not visible.

| Method and path | Behavior |
| --- | --- |
| `POST /` | Validate and persist a directed declaration; return `202 Accepted` with pending targets |
| `GET /{id}` | Read current connection and per-target reconciliation state |
| `GET /?pageNumber=1&pageSize=10` | List the tenant's connections; page size is bounded to 100 and the offset must fit a 32-bit integer |
| `DELETE /{id}` | Request cleanup; return `202 Accepted` while deletion is pending |

Example creation body:

```json
{
  "name": "orders-cache",
  "sourceResourceType": "pole/web-service",
  "sourceResourceId": "507f1f77bcf86cd799439011",
  "destinationResourceType": "pole/redis",
  "destinationResourceId": "507f1f77bcf86cd799439012",
  "endpointId": "redis"
}
```

There is no update operation for declaration identity. Create a new connection when those
references change. An accepted declaration has not yet established network access. `Applied`
means the policy was accepted by the target API, not that an end-to-end application request or
CNI convergence has been verified. Target transport failure remains retryable reconciliation
state; it is distinct from invalid placement topology.

## Reconciliation and deletion

Outbox consumers apply one policy per destination namespace and connection, named
`pole-connection-{connectionId}`. The destination selector includes tenant, runtime-cell, and
resource-deployment identity. Each allowed source peer combines its namespace identity and pod
identity in the same Kubernetes peer, so an identically labelled pod in another namespace cannot
match. The rule permits TCP to the endpoint's pod target port. A service exposing port 80 with
target port 8080 therefore returns port 80 to clients and permits port 8080 in the policy.

The writer ensures the destination baseline before allowing traffic. It checks current authority
around cluster writes and uses Kubernetes resource versions and UID preconditions to avoid
changing a foreign or newer policy. Periodic recovery retries failed work and rechecks applied
policies for drift. A new generation supersedes older work. Connections are independently applied
per target; there is no atomic operation across clusters.

Deleting either logical resource marks its connections `Deleting` within the resource-deletion
transaction before requesting workload deletion. Cleanup uses complete retained placement
snapshots, so it does not need the deleted resource, endpoint catalog, or deployment rows.
Unavailable targets keep cleanup pending. An absent policy or namespace is already cleaned.

After all cleanup succeeds, the public connection is removed transactionally with a hidden cleanup
tombstone. Tombstones retain tenant, connection ID, and current plus retired policy locations in a
separate collection.
They are retained for periodic repair of an older in-flight write or a crash between a policy
write and its authority recheck. They do not reserve the public name, and cannot affect a new
connection that reuses that name with a different ID. This currently has no time-based retention
limit. Successful cleanup and deletion do not promise immediate termination of established TCP
sessions or eliminate the temporary interval before CNI convergence or recovery.
Each recovery cycle revisits these tombstones, so historical deletions retain Mongo metadata and
generate cleanup reads; there is currently no garbage-collection policy.

## Ownership and verification

`ResourceConnections.Domain` owns lifecycle and generation transitions. `ResourceConnections.Contracts`
publishes transaction-joining mutation and deletion operations. Application owns Mongo persistence,
HTTP endpoints, outbox consumers, Kubernetes policies, and the recurring recovery job. Redis,
WebServices, and Postgres own their endpoint catalog contracts and implementations.

Focused tests cover tenant and role boundaries, exact target pairing, immutable identity,
transaction rollback, endpoint refresh, cascade snapshots, stale writes, and recovery. Live CNI
tests use ordinary pods, establish positive controls, and check exact source identity, direction,
service-to-target port mapping, excluded ports, and revocation. Before publication, run the full
[local quality gate](../../../docs/agent-delivery.md); focused tests are development evidence.

See [resource groups](../ResourceGroups/README.md), [WebServices](../WebServices/README.md),
[deployment targets](../../../docs/deployment-targets.md), and
[module boundaries](../../../docs/module-boundaries.md).
