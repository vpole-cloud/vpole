# Private access

PrivateAccess owns tenant grants and short-lived session authorization for an exact workload
deployment endpoint. It does not own Redis or PostgreSQL destinations. Providers publish their
server-owned endpoint IDs and service names through `IResourceConnectionDestinationCapability`.

## Control-plane API

All routes require an authenticated user and `X-Pole-Tenant-ID`.

| Route | Access | Result |
| --- | --- | --- |
| `POST /api/v1/private-access/grants` | Current tenant Owner or Admin | Creates a grant for a current tenant member |
| `GET /api/v1/private-access/grants` | Current member | Lists the caller's active grants |
| `GET /api/v1/private-access/grants/manage` | Current tenant Owner or Admin | Lists tenant grants, including expired and revoked grants |
| `GET /api/v1/private-access/grants/{id}` | Subject or current tenant Owner or Admin | Gets grant metadata |
| `DELETE /api/v1/private-access/grants/{id}` | Current tenant Owner or Admin | Revokes a grant |
| `POST /api/v1/private-access/grants/{id}/sessions` | Grant subject, still a current tenant member | Issues one session ticket |

Grant creation accepts subject user ID, resource type and ID, exact deployment ID, endpoint ID,
and UTC expiry. The API never accepts or returns a raw destination address. Grant and session
responses contain metadata only, except the session issuance response, which returns an opaque
one-use ticket once with `Cache-Control: no-store`. Only its SHA-256 hash is persisted.

## Authorization and lifecycle

Grant writes verify the actor's actual current tenant role, including when the caller also has a
platform administrator claim. Creation write-fences membership, registration, and exact deployment
inside one Mongo transaction before writing the grant, audit row, and outbox event. A grant has
explicit Active and Revoked BSON states and a monotonically increasing revision.

Session issuance binds the current grant revision, subject, exact deployment revision and
generation, target, runtime cell, namespace, and provider endpoint to a session. Issuance also
reserves a deployment-target reference until worker cleanup completes. The ticket expires within
30 seconds and may be redeemed only once. Every redemption, setup authorization, start, and
renewal checks current grant, membership, resource registration, deployment state and binding,
provider endpoint, and target network-policy enforcement. The target must be enabled, available,
and explicitly attested as enforcing NetworkPolicy; an isolation override does not qualify.

The broker-facing Contracts return leases of at most 10 seconds. Active renewals cannot revive
an expired lease, and the broker and worker must enforce lease expiry locally. Setup may request
fresh authorization while a redeemed ticket waits up to 30 seconds for its worker; the worker
must not dial the destination until the broker has approved that fresh challenge. Renewal never
changes a session's destination binding. Grant revocation, membership removal, target isolation
withdrawal, or changed placement invalidates the next authorization.

Session states are Pending, Redeemed, Active, Terminated, Cleaning, and Cleaned. Cleanup page reads
atomically claim eligible sessions into Cleaning before returning exact worker identity, so a stale
read cannot delete a worker whose lease renewed. A crashed cleanup run can retry Cleaning
sessions. Marking cleanup complete releases the target reference in the same transaction. Session
documents are eligible for TTL deletion only 30 days after that acknowledgment; audit metadata
remains separate. Audit records never contain tickets, destination credentials, or database data.

## Verification

Focused Mongo integration tests under `tests/Pole.Tests.Integration/PrivateAccess` cover grant
atomicity, tenant roles, one-use tickets, replay, binding and attestation changes, cleanup claims,
and target-reference admission. The repository's complete quality gate remains the publication
check for the integrated server, broker, connector, frontend, and infrastructure lanes.
