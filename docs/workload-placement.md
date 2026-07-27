# Workload placement

Pole separates tenant organization, isolation, and physical Kubernetes placement:

```text
Tenant
└── ResourceGroup
    ├── IsolationScope (default)
    │   ├── RuntimeCell → DeploymentTarget A → namespace in cluster A
    │   └── RuntimeCell → DeploymentTarget B → namespace in cluster B
    └── IsolationScope (additional)
        └── RuntimeCell → DeploymentTarget A → separate namespace
```

`ResourceGroup` is the tenant-owned organizational aggregate. `IsolationScope` is the
logical isolation boundary. `RuntimeCell` is the durable placement of one scope in one
deployment target and is the authoritative owner of that cluster's namespace.

## Isolation scopes

Resource-group creation inserts the group and its default scope in one Mongo transaction.
It does not create a runtime cell. During the current transition, however, the existing
handler still admits against the default target and the existing
`ResourceGroupCreatedEvent` consumer provisions the legacy resource-group namespace. Those
compatibility side effects remain until WebService, Redis, PostgreSQL, and Functions stop
using that namespace in #37/#55.

The default scope ID is immutable and stored by the resource group. Additional scope names
are required, normalized for uniqueness within a resource group, and immutable. The default
scope cannot be deleted separately.

Tenant APIs are nested under the owning resource group:

```http
GET    /api/v1/resource-groups/{resourceGroupId}/isolation-scopes
POST   /api/v1/resource-groups/{resourceGroupId}/isolation-scopes
GET    /api/v1/resource-groups/{resourceGroupId}/isolation-scopes/{scopeId}
DELETE /api/v1/resource-groups/{resourceGroupId}/isolation-scopes/{scopeId}
```

Requests bind route, body, and `X-Pole-Tenant-ID` values through `[AsParameters]`.
Deterministic input validation uses FluentValidation. Handler lookups always combine tenant,
resource-group, and child IDs, so cross-tenant and cross-group probes return safe not-found
results.

## Runtime cells and placement

A runtime cell is unique by `(IsolationScopeId, DeploymentTargetId)`. It stores:

- tenant, resource-group, scope, and target IDs;
- immutable Kubernetes namespace;
- desired and observed generation;
- explicit lifecycle state;
- creation and update timestamps.

Resource features resolve placement through `IResourcePlacementService`. The caller supplies
its Mongo session so resource mutation, runtime-cell creation, target-reference registration,
and the first outbox event can share one transaction. A missing scope ID selects the default
scope; a missing target ID selects the default eligible target.

New placement requires an active resource group and scope and a target that accepts new
workloads. An existing cell remains usable when its target stops accepting new workloads;
this allows reconciliation and cleanup without silently moving the workload to another
cluster. Parent revision writes fence placement against concurrent scope or resource-group
deletion.

## Namespace ownership

Runtime-cell namespace names use:

```text
<prefix>-t-<tenant8>-is-<scope24>
```

The prefix is lowercased, restricted to DNS-label characters, trimmed, and limited to 24
characters. The tenant fragment uses eight ObjectId characters and the scope fragment uses
the full 24-character ObjectId. The complete name is at most 63 characters and is stable for
the same scope across deployment targets.

Every namespace carries:

```text
pole.io/tenant-id
pole.io/resource-group-id
pole.io/isolation-scope-id
pole.io/runtime-cell-id
pole.io/deployment-target-id
```

The namespace manager obtains its Kubernetes client from the persisted deployment target.
An absent namespace is created. An existing namespace with the same complete ownership
labels is an idempotent success; missing or different ownership labels produce the sanitized
`NamespaceOwnershipConflict` failure. Namespace delete treats Kubernetes `404` as success.
Provider credentials and raw provider exception messages are never stored in runtime cells
or durable events.

## Lifecycle and recovery

Runtime-cell states are:

```text
Provisioning → Ready
Provisioning/Deleting → Failed
Failed → Provisioning or Deleting
Deleting → Deleted
```

Every operation is generation-fenced. Consumers load the current document and ignore stale
or duplicate events. Successful state changes and their events commit together through the
Mongo outbox. Expected Kubernetes failures become sanitized, retryable state; unexpected
infrastructure failures remain failures so normal delivery retry and observability apply.

The operational API can list/get cells and explicitly retry a failed cell. A bounded Hangfire
job republishes requests for stale provisioning/deleting cells and advances due failed cells.
It also scans lifecycle-indexed deleting scopes to close the crash window between successful
cell persistence and aggregate finalization. The job does not call Kubernetes directly.

## Deletion and finalization

Deleting an empty additional scope completes synchronously with `204 No Content`. If the
scope owns unfinished runtime cells, the API returns `202 Accepted`, marks the scope and
cells as deleting, and writes deletion events in the same transaction.

Resource-group deletion currently uses `ResourceGroup.Resources` as a temporary compatibility
guard. A non-empty registration list returns `409 Conflict`. That list is not yet an
authoritative cross-feature registration source and has an asynchronous update window.
Issue #54 must replace it with platform-owned generic registration before this deletion path
is treated as production-safe.

For an otherwise empty group, deletion completes synchronously. If runtime cells need
cleanup, the group and all scopes/cells transition to deleting and the API returns `202`.
RuntimeCell placement, including reuse of an existing cell, performs a parent write fence
and cannot commit against that transition. The transitional legacy lookup rejects a group
already observed in `Deleting`, but it does not provide the caller-owned transaction fence
that ResourceDeployment adoption will provide.

Each deletion consumer:

1. verifies cell ID, tenant, generation, and deleting state;
2. deletes the namespace through the persisted target;
3. atomically marks the cell deleted, unregisters its `DeploymentTargetReference`, and writes
   `RuntimeCellDeletedEvent`;
4. asks the finalizer to remove the scope when no unfinished cells remain;
5. removes the resource group and writes the final `ResourceGroupDeletedEvent` only after all
   scopes and cells are gone.

Failed cleanup leaves the group, scope, and runtime cell visible. Explicit retry and the
recovery job resume from durable state. There is no force-delete path.

## Deployment-target references

`DeploymentTargetReference` is a transactional referential-integrity index. Runtime cells
register with:

```text
OwnerType = RuntimeCell
OwnerId   = <runtime-cell-id>
```

The reference blocks deployment-target deletion from cell creation until namespace cleanup
succeeds. It is not the placement source of truth; the RuntimeCell is.

## Transition boundary

This placement foundation intentionally does not introduce `ResourceDeployment`.
WebService, Redis, and PostgreSQL adoption belongs to issue #37. Functions adoption belongs
to issue #55. Until those changes land, their existing handlers continue to consume the
legacy `ResourceGroup.KubernetesNamespace` through the explicitly transitional
`IResourceGroupLookup.GetPlacementAsync` contract.

New code must use `GetOwnershipAsync` and `IResourcePlacementService`; it must not add new
dependencies on the legacy namespace field. No production-data migration or backward
compatibility code is included because Pole has not yet deployed this model.
