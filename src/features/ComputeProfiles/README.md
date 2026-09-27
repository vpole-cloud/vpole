# Compute profiles

ComputeProfiles is the platform-owned catalog of portable workload compute and Kubernetes
scheduling policy. Tenants select a profile; they do not submit raw CPU, memory, node labels,
tolerations, or runtime classes with each resource.

## Owned model

A profile contains:

- CPU and memory requests and limits;
- optional extended resources and an architecture constraint;
- allowed Pole resource types and either all-tenant or selected-tenant access;
- one or more deployment-target mappings with node selector, required/preferred node affinity,
  tolerations, and an optional runtime class;
- enabled/default state, configuration revision, and a resolution fence.

The profile is reusable policy, not mutable workload state. When a workload resolves a profile,
`ResourceDeployment` stores a complete immutable compute snapshot. Later profile edits therefore
do not silently change existing workloads.

## API and tenant boundary

Platform admins manage the catalog through:

```http
GET    /api/v1/platform/compute-profiles
POST   /api/v1/platform/compute-profiles
GET    /api/v1/platform/compute-profiles/{id}
PUT    /api/v1/platform/compute-profiles/{id}
DELETE /api/v1/platform/compute-profiles/{id}
```

Tenant callers list profiles currently available for a resource type and deployment target:

```http
GET /api/v1/compute-profiles/available?resourceType=...&deploymentTargetId=...
X-Pole-Tenant-ID: <tenant-id>
```

Availability requires an enabled profile, an allowed resource type, tenant access, and a mapping
for the selected target. Admin authorization does not replace these contextual checks during
workload resolution.

## Resolution and adoption

Create requests may omit `ComputeProfileId`; omission selects the current default profile.
Supplying an ID resolves that profile for the tenant, resource type, and already-resolved target.

On update:

- omitted `ComputeProfileId` preserves the deployment's existing snapshot;
- supplied `ComputeProfileId`, including the current ID, resolves and adopts the latest profile
  revision.

Resolution and resource mutation are separated by a Mongo transaction boundary. The profile's
resolution fence is incremented when the workload transaction commits, so concurrent profile
update/delete cannot invalidate a snapshot that was admitted from stale configuration.

## Persistence and target integrity

The Application project exposes narrow named Mongo capabilities rather than a feature-wide
database context. Create, update, default promotion, and delete are revision-fenced. Promoting a
new default demotes the previous default atomically; the active default cannot be directly
disabled, demoted, or deleted.

Every target mapping owns a transactional `DeploymentTargetReference` with owner type
`compute-profile`. Mapping changes register and unregister those references in the same
transaction as the profile change, preventing target deletion from leaving dangling mappings.
Deletion is also blocked while any `ResourceDeployment` snapshot references the profile.

Mongo schema and index migrations are self-contained historical contracts. Startup runs target
bootstrap first, then creates the built-in default profile if the catalog is empty. Existing
administrator-created defaults are never overwritten. Bootstrap resolves the configured target
without workload admission: a fresh target can remain `Unknown` until an administrator attests
NetworkPolicy enforcement through the running API. Compute-profile references still require an
enabled target accepting workloads and fence target deletion, but do not require attestation.
Actual workload admission and runtime-reference creation remain blocked until attestation or an
explicit isolation-not-enforced override.

## Operation outcomes and HTTP mapping

Owned validation, persistence, resolution, and fencing capabilities return named operation
unions. Mutation endpoints expose their concrete success and error cases and call HTTP mapping
methods directly, including methods from provider-owned `Contracts.HttpMapping` projects.

Resolution retains `ComputeProfileResolutionError` as the shared error group for workload
consumers. Its contract HTTP mappings remain registered while those consumers still use the
dynamic mapper. Calls to DeploymentTargets and ResourceDeployments retain those providers'
existing result contracts until their respective migrations.

## Kubernetes validation and limitations

Target mappings are checked against the selected cluster's node labels and allocatable extended
resources. RuntimeClass selectors participate in that check. Preferred affinity and taints are
not treated as hard capacity filters; this is configuration/capability validation, not a live
scheduler simulation.

WebService, Redis, and native StatefulSet PostgreSQL manifests consume the entire resolved
snapshot. CloudNativePG supports resources and affinity/tolerations but its Cluster CRD has no
`runtimeClassName`; operator-mode PostgreSQL therefore rejects such a profile explicitly rather
than dropping the setting.

The current frontend does not yet expose catalog management or per-resource profile selection.
Resources created through that UI rely on default-profile resolution.

## Verification

Coverage lives under `tests/Pole.Tests.Integration/ComputeProfiles` and the adopting feature test
folders. It includes domain normalization, FluentValidation, strict Mongo schema, indexes,
default promotion, target references, tenant/resource availability, resolution fencing, cluster
capability matching, quota values, and generated manifests.

Related documentation:

- [Workload placement](../../../docs/workload-placement.md)
- [Deployment targets](../../../docs/deployment-targets.md)
- [Tenant quotas](../../../docs/quotas.md)
- [Testing](../../../docs/testing.md)
