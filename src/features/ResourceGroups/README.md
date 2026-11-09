# Resource groups

ResourceGroups owns tenant organization and the platform-wide registry that associates logical
resources with a group. It does not own resource-specific state or Kubernetes realization.

## Owned data and boundaries

- `ResourceGroup` stores metadata, lifecycle, its default isolation scope, and a registration
  concurrency fence. It does not embed resources.
- `ResourceRegistration` stores `TenantId`, `ResourceGroupId`, stable `ResourceTypeId`, opaque
  resource ID, display name, optional plugin owner, and timestamps.
- First-party resource types use the reserved `pole/*` namespace. Third-party registrars are bound
  by trusted application composition to one normalized plugin ID, and their resource-type namespace
  must match that ID.
- Resource owners depend only on `ResourceGroups.Contracts`. They register and unregister through
  an owner-bound `IResourceRegistrar` that requires the owner's active Mongo session.

The unique `(TenantId, ResourceTypeId, ResourceId)` index makes a resource identity stable within
a tenant. The `(TenantId, ResourceGroupId, ResourceTypeId, ResourceId)` index supports membership
guards and ordered listing without scanning concrete resource collections.

## Transaction and deletion rules

Registration is committed in the same transaction as logical resource creation. Unregistration
is committed only with final logical-resource deletion, after infrastructure deletion is ready.
Exact repeated registration and missing unregistration are idempotent. Reusing an identity for a
different group or plugin is a typed conflict.

Every registration mutation advances the parent group's registration fence. ResourceGroup
metadata replacement and deletion compare that fence, so a concurrent registration cannot be
overwritten and cannot race an empty-group deletion.

Web Services, Redis, PostgreSQL, and Functions currently use these contracts. Adding a resource
type requires a stable feature-owned `ResourceTypeId` constant and the same create/final-delete
transaction hooks; ResourceGroups must not reference that feature's Domain, Application, or
Events assembly.

Accepted registration, display-name change and final unregistration write ID-only lifecycle events
in the resource owner's transaction. Repeated no-ops and typed conflicts emit no event. Owning
realtime consumers translate committed events into group and workspace-inventory invalidations;
HTTP remains authoritative for names, counts and resource details.

## RuntimeCell deletion progress

An owned namespace that still exists after DELETE leaves its RuntimeCell in `Deleting` at the
same generation. Persistence advances a check sequence and atomically stores the next check time
with its outbox request. The five-second due time is rounded up to BSON millisecond precision
before both state and event are created, so JSON dispatch and persisted authority agree exactly.
Scheduled checks validate tenant, generation, sequence and due time; compare-and-swap prevents
concurrent or stale results from advancing the same check twice.

Only confirmed native absence permits the transaction that marks the cell deleted and releases
its deployment-target reference. ResourceGroup finalization follows that commit. The minutely
recovery scanner restores lost dispatches and still accepts legacy deleting documents and events
without scheduling fields. Actual infrastructure and ownership failures keep their existing
failure/retry path; ordinary deletion progress does not enter that backoff.

## RuntimeCell ingress baseline

ResourceGroups also owns the RuntimeCell namespace lifecycle. It installs an ingress-only
deny-all NetworkPolicy in every RuntimeCell namespace before marking the cell Ready or allowing
workload application. The policy selects all pods in the cell, including pods introduced later
by other controllers, while leaving egress unrestricted. Reconciliation checks policy ownership
before repairing it; a conflicting owner or failed policy write keeps the cell unavailable for
new workload application. Ready cells are requeued for baseline verification after five minutes,
including cells with no deployments. This is bounded repair of recorded desired state, not a
claim that CNI enforcement converges synchronously or that externally deleted policies are
repaired before any packet can arrive.

## Tenant API

`GET /api/v1/resource-groups?pageNumber=1&pageSize=20&search=prod` returns a tenant-scoped
`PaginatedResponse<ResourceGroupDto>`; search matches group names. `GET /api/v1/resource-groups/{id}`
reads a group directly for workspace links independently of its inventory page. Missing and
foreign-tenant IDs both return 404. Both endpoints propagate request cancellation.

Group responses expose `resourceCount`; lists load counts for the complete page with one grouped
registration query. Registrations are listed with:

```http
GET /api/v1/resource-groups/{resourceGroupId}/resources?pageNumber=1&pageSize=20
X-Pole-Tenant-ID: <tenant-id>
```

The endpoint returns a tenant-scoped page ordered by resource type and resource ID. Registration
mutation APIs are intentionally not exposed.

## Compatibility

Issue #54 intentionally has no data migration or dual-read path. Existing development databases
that contain embedded `ResourceGroup.Resources` or enum-based Storage Profile resource types must
be reset; see [Aspire local development](../../../docs/aspire-local-development.md#resetting-data-after-a-schema-breaking-change).

Related platform behavior is documented in [workload placement](../../../docs/workload-placement.md)
and [multitenancy](../../../docs/multitenancy.md).
