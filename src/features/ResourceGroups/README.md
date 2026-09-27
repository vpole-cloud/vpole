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

Registration lifecycle events are intentionally absent because no asynchronous projection or
external consumer currently needs them.

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

ResourceGroup list responses expose `resourceCount`, loaded for the complete page with one grouped
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
