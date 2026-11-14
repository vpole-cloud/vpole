# PostgreSQL clusters

The PostgreSQL provider exposes tenant-scoped CRUD, administrator credential and rotation endpoints.
`GET /api/v1/postgres/supported-versions` returns the supported version range.

`Postgres.Tests`
owns infrastructure-free domain, credential, quota, HTTP-mapping, manifest, registration, and fake
Kubernetes observation checks. `Postgres.Tests.Integration` owns Mongo and HTTP/outbox checks.
`Postgres.Tests.Kubernetes` runs live CloudNativePG and observation checks against
the reusable lab described in `docs/testing.md`.

Pole provisions each logical PostgreSQL cluster through CloudNativePG.

## Backup and recovery

The [Backups feature](../Backups/README.md) owns tenant policies, S3 destinations and operation history.
The PostgreSQL provider configures a Barman Cloud `ObjectStore`, the WAL archiver plugin on the
source CNPG `Cluster`, and a `ScheduledBackup`. Its six-field cron starts with zero seconds.
CloudNativePG 1.30 schedules backups in the operator's local time zone. Run the operator in UTC
(the stock image default) to execute Pole's UTC schedules. Suspending the schedule keeps WAL archiving,
retention and manual copies available. Each manual operation uses its ID in the CNPG `Backup` name, so another worker can resume
observing the same object. Kubernetes resource versions and policy revision annotations fence delayed
writes. Superseded schedules remain suspended until Kubernetes garbage-collects them with the source.

The control plane reads each catalog directly from the tenant's S3 endpoint. The list contains completed
Barman backup IDs and their completion times. Failed requests and unreadable catalog entries produce
an error. The endpoint must be reachable from the control plane as well as from the workload cluster.

Recovery creates a new manually managed PostgreSQL resource in the original RuntimeCell. Its persisted
bootstrap selects either the exact backup ID and immediate consistency target, or the requested UTC
timestamp. CNPG replays the available WAL and reports a failed recovery if it cannot reach that target.
The new cluster receives its own administrator password. Its bootstrap keeps using the source archive
as a recovery input. A separate backup policy can enable WAL archiving for the new cluster.

## Workload model

`PostgresCluster` stores the logical database configuration, version, topology, storage choice,
and lifecycle state. Credentials are managed by the [Secrets feature](../Secrets/README.md). Kubernetes placement and its resolved infrastructure configuration are
stored in `ResourceDeployment`.

Provisioning is separate from deletion lifecycle. Manual creation explicitly initializes `Direct` provisioning.
The declarative construction path initializes `Prepared` with a pinned bootstrap Secret/key and managed credential ID. Prepared clusters cannot produce workload manifests, apply ingress or Secrets,
or establish Kubernetes observation sessions. Their placement is deferred and pins the selected catalog revisions.

`PostgresBootstrapInitializer` runs inside the owner mutation transaction. Secrets verifies the bootstrap grant
and copies the initial password into the encrypted owner credential. Only then does the initializer return a
`Configured` replacement for the caller to persist and activate in that same transaction. An initialized credential
is never recopied after password rotation; a missing configured credential blocks instead of restarting bootstrap.
Every logical mutation also checks `PlanningFence`, preserving conflicts with concurrent planning guards.

CloudNativePG manages replica counts and volume growth.

Create resolves the default or requested `ComputeProfile` for the active tenant and target. The
deployment stores the resulting CPU, memory, extended-resource, and scheduling snapshot. Update
without a profile ID preserves that snapshot; an explicit ID adopts the latest eligible profile
revision. Delete and password rotation fence their mutations against the current snapshot.

Configuration updates require a positive `expectedRevision` matching the logical database
revision. A stale draft receives `409 postgres.update.concurrent-change` before quota admission
or mutations; persistence also retains its transaction revision fence. Omitted `instances`,
`storage`, and `computeProfileId` preserve the current server values. The browser submits only
changed fields and requires explicit review before reapplying a draft to a newer revision.

Quota admission measures the resolved per-instance CPU and memory requests across the requested
instance count. CloudNativePG supports resources, node selection, affinity, and tolerations.
Its Cluster API rejects profiles that require a runtime class.

The RuntimeCell baseline denies ingress before a PostgreSQL workload starts. Each deployment
gets a replication policy limited to pods with the same tenant, resource, and deployment IDs on
TCP 5432. CloudNativePG mode also requires an explicit target `PostgresOperator` network source
and grants that source TCP 5432 and 8000. Missing operator identity or a failed policy write
blocks reconciliation with a retryable status; Pole does not open an entire platform namespace.

## Repository GitOps

`PostgresCluster` declarations use `spec.resourceGroup`, `placement.deploymentTarget`, a pinned
`placement.computeProfile: {name, revision}`, optional `placement.isolationScope`, and
`storage: {sizeGb, profile: {name, revision}}`. Version is an integer. `instances` defaults to one.
`bootstrapPassword: {secret, key}` supplies the initial password for the SQL superuser `postgres`.
A `SecretBinding` for the `PostgresCluster` consumer
must explicitly declare `usage: Bootstrap`; it grants initialization without creating a runtime
projection of the source Secret.

The provider follows Prepare → Bind → Configure. Prepare creates an inert root and deferred
placement; Configure accepts the latest desired capacity and encrypted credential together in one
revision. Group, scope, target, version, bootstrap reference and storage profile/revision
are immutable. Instance count, storage growth and switching compute profile revisions follow the ordinary update workflows.

Polling and Sync compare the actual managed placement and capacity with the accepted snapshot,
including at unchanged commits. A verified no-op keeps business revisions, credentials and events
unchanged. Every operation fences the root, registration, placement, scope and current catalog policy.
Configured planning fences only the owner credential metadata: later source value changes cannot
recopy an initial password. Missing created roots or initialized credentials block the run.

The ordinary update and delete paths reject Git-owned configuration inside their transactions.
Detail/list DTOs expose GitOps metadata; the browser keeps configuration read-only while preserving
credential operations.

## Persistence and reconciliation

Mongo persistence and outbox events remain feature-owned. Kubernetes reconciliation routes every
operation through the deployment's persisted target, namespace, storage, and compute snapshot.
PostgreSQL has no separate credential collection or Kubernetes Secret writer. The Secrets feature
owns encrypted immutable versions, explicit bindings, per-deployment runtime projections and their
cleanup. PostgreSQL calls its provider-owned contracts and retains only transient credential values
for the authorized connection-string operation.

## Credential lifecycle

Creation prepares one encrypted managed Secret with `username` and `password` keys and the explicit
basic-auth projection type. Combined credential values are limited to 64 KiB of UTF-8 data;
rotation includes the retained username when validating this limit. Within the existing quota transaction PostgreSQL stages its logical
resource, registration and ResourceDeployment, then asks Secrets to create the owner binding against
that staged placement. The namespace or workload need not be ready. A failure aborts the resource,
Secret, deployment, quota changes and outbox records together.

Reconciliation requests the managed projection for the exact tenant, owner and ResourceDeployment.
Secrets applies its generated native Secret to the persisted target and RuntimeCell namespace.
Only after that projection is ready does PostgreSQL apply the workload using the returned reference;
CloudNativePG reads the projected Secret through `spec.superuserSecret` with `spec.enableSuperuserAccess: true`.
Clients receive a single connection string for the `postgres` superuser and the `postgres` database.
The owner creates application roles and databases. A missing projection
returns a retryable deployment failure. Each projection has its own desired/applied version and
failure status in Secrets; a Kubernetes Secret remains a runtime copy, not the authoritative value.

The password endpoint reads the managed version and prepares a replacement preserving the username.
Rotation and the ResourceDeployment apply request commit in one Mongo transaction with an expected
Secret version. CloudNativePG reconciles the projected superuser password on the cluster.
Generic Secret mutation cannot bypass the owning PostgreSQL rotation policy.

Final owner deletion releases its managed Secret in the same transaction as resource finalization.
If no other consumer remains, Secrets removes the encrypted value and metadata. A Secret still bound
to another consumer remains as an ordinary tenant Secret, with its other projections intact.
Connection-string reads continue to require a tenant write role and resolve credentials through the
owning PostgreSQL identity; callers never supply a native Kubernetes Secret name.

Native Kubernetes Secret values are readable by authorized Kubernetes clients and consuming pods.
Encryption of target etcd storage is the cluster operator's responsibility.

Related documentation:

- [Compute profiles](../ComputeProfiles/README.md)
- [Workload placement](../../../docs/workload-placement.md)
- [Tenant quotas](../../../docs/quotas.md)
