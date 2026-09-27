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
It does not admit a deployment target, create a namespace, or create a runtime cell.
Runtime cells and their namespaces are created lazily when a workload resolves placement.

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

## Resource deployments and compute

WebService, Redis, and PostgreSQL use `ResourceDeployment` as the durable binding between a
logical resource and its physical placement. The document records the resource identity,
runtime cell, target, namespace, resolved ingress/storage values, reconciliation generation, and
a complete immutable compute snapshot.

Compute comes from a platform-managed `ComputeProfile`. Resolution is contextual: the profile
must be enabled, accessible to the tenant, allow the resource type, and have a mapping for the
selected target. Omitting a profile on create selects the default. Omitting it on update preserves
the existing snapshot; supplying an ID explicitly adopts the latest eligible revision.

Resource mutation compares the expected deployment snapshot and fences the resolved profile in
the same Mongo transaction as quota usage, logical resource persistence, deployment persistence,
and outbox writes. Concurrent profile changes or resource updates therefore fail cleanly instead
of releasing quota or applying manifests from stale compute configuration.

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

Reconciliation and deletion requests use a centralized logical pending key containing the
runtime-cell ID, generation, and operation. The outbox enqueues that key atomically and treats
an already-pending key as a successful no-op. Recovery therefore neither scans nor
deserializes outbox payload JSON, and concurrent recovery cannot create multiple active
requests for the same operation and generation. Publishing or structurally abandoning a
message releases its key so a later recovery pass can enqueue a fresh request.

## Deletion and finalization

Deleting an empty additional scope completes synchronously with `204 No Content`. If the
scope owns unfinished runtime cells, the API returns `202 Accepted`, marks the scope and
cells as deleting, and writes deletion events in the same transaction.

Resource-group membership is authoritative in the ResourceGroups-owned `ResourceRegistrations`
collection. Logical resource creation registers `(TenantId, ResourceTypeId, ResourceId)` in the
same Mongo transaction as the resource; final deletion unregisters it in the same transaction as
the logical delete. A non-empty group returns `409 Conflict`. The indexed membership read and a
parent registration fence prevent both cross-feature scans and registration-versus-deletion races.
See the [ResourceGroups feature guide](../src/features/ResourceGroups/README.md).

For an otherwise empty group, deletion completes synchronously. If runtime cells need
cleanup, the group and all scopes/cells transition to deleting and the API returns `202`.
RuntimeCell placement, including reuse of an existing cell, performs a parent write fence
and cannot commit against that transition. ResourceDeployment creation and mutation use that
caller-owned transaction fence rather than a resource-group namespace lookup.

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

## Placement boundary

WebService, Redis, PostgreSQL, and Functions reconcile through `ResourceDeployment`. New resource
features must use the ResourceDeployment placement, mutation, and fence capabilities; they must
not add Kubernetes placement fields to logical resource aggregates. No production-data migration
or backward-compatibility path is included because Pole has not yet deployed the old model.

### Kubernetes authorization identity

RuntimeCell namespaces carry immutable `pole.io/tenant-id`, `resource-group-id`,
`isolation-scope-id`, `runtime-cell-id`, and `deployment-target-id` labels (all under `pole.io/`).
Workload pod templates and Services additionally carry `resource-type`, `resource-id`, and
`resource-deployment-id`. WebService, Redis, PostgreSQL, and Function reconciliation propagate
these identities from persisted placement. Generated selectors use concrete resource/deployment
IDs rather than names or deployment generations. A resource's `Name` is immutable during its
lifetime, but deletion and recreation can reuse that name with a new ID. The new instance must
not match the previous instance's workload selectors or inherit its durable bindings.

Every RuntimeCell namespace has an owned ingress-only deny-all NetworkPolicy before any
workload is applied. Reconciliation checks the persisted tenant, cell, target, and namespace
and repairs the baseline before applying a workload or positively scaling a Function revision.
A failed policy write is a retryable reconciliation failure, never a Ready claim. The baseline selects all
pods in the cell, including pods created later by other controllers. It does not restrict
egress. Workload policies select stable tenant/cell/deployment ID labels, so sharing an
IsolationScope or RuntimeCell never grants traffic between resources.

Provider-owned exceptions are narrow: a Public WebService permits only the configured
ingress controller source to its numeric pod target port; its ACME HTTP01 solver policy
permits that same source to solver-labeled pods on port 8089. Functions permit the configured
gateway source to their Agent port 8080. PostgreSQL permits same-deployment replication on
5432 and, for CloudNativePG mode, the configured operator source on 5432 and 8000. Missing
sources fail closed. Policy ownership labels and independent deployment-generation/target-
configuration revisions fence retries and source rotations. A bounded recovery scan selects
Applied deployments for verification after five minutes; the existing outbox, claim, and retry
path re-applies the same desired generation and logical resource revision. Ready RuntimeCells
are likewise requeued for baseline repair after five minutes, including empty cells. This is
scheduled repair, not an immediate revocation or convergence guarantee.

WebServices default to `Private`. A Private service has no public hostname reservation,
Ingress, or public endpoint, and its pods remain behind the RuntimeCell ingress deny rule.
Choosing `Public` requires a public ingress source and resolved ingress configuration on
every selected target. Switching a service from Public to Private removes its public Ingress
and revokes both the application and HTTP01 policy allowances; switching back reserves a
public hostname before applying the public deployment. A target-source change is picked up
by the bounded reconciliation scan, which closes an obsolete allowance before reporting a
missing replacement source.

The agreed IaC-facing address is `(tenant, resource type, normalized name)`, represented as
`kind + name` (or `kind/name`); typed reference fields may omit the kind. Group and isolation
scope remain ownership/admission constraints, not name qualifiers. Providers enforce naming
uniqueness in their own collections; this change does not introduce a name-resolution API or
claim that every existing provider has completed naming alignment. Internal references and
runtime selectors retain IDs. Human-readable names remain available in resource metadata.
PostgreSQL operator resources propagate the labels through `inheritedMetadata`.
