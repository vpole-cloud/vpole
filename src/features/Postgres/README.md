# PostgreSQL clusters

The frontend PostgreSQL provider uses the existing tenant CRUD, credential and rotation endpoints.
`GET /api/v1/postgres/supported-versions` returns the version range and additive
`defaultDeploymentMode` metadata (`Operator` or `StatefulSet`). Creation uses this configured mode;
the UI does not offer a client-side mode override.

`Postgres.Tests`
owns infrastructure-free domain, credential, quota, HTTP-mapping, and manifest checks;
`Postgres.Tests.Integration` owns Mongo, HTTP/outbox, and live Kubernetes checks. The tests use the
reusable lab described in `docs/testing.md`; they validate an existing lab but do not provision it.

The PostgreSQL feature owns tenant-scoped logical database clusters and supports two deployment
modes: a native Kubernetes StatefulSet and the CloudNativePG operator.

## Workload model

`PostgresCluster` stores the logical database configuration, version, topology, storage choice,
and lifecycle state. Credentials are managed by the [Secrets feature](../Secrets/README.md). Kubernetes placement and its resolved infrastructure configuration are
stored in `ResourceDeployment`.

The browser keeps native StatefulSet storage fixed after creation. Native reapply preserves the
existing immutable claim template (including its initial generation labels), so compute changes
and new pod-template generations do not attempt to mutate that template. Operator-mode storage
growth remains controlled by CloudNativePG.
API updates reject native capacity changes before persistence. Existing failed resize requests
remain failed during reapply rather than reporting unchanged physical storage as applied.

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
instance count. Native StatefulSet deployments support the complete scheduling snapshot.
CloudNativePG supports resources, node selection, affinity, and tolerations; because its Cluster
API does not support runtime class, an incompatible operator-mode request is rejected explicitly.

The RuntimeCell baseline denies ingress before a PostgreSQL workload starts. Each deployment
gets a replication policy limited to pods with the same tenant, resource, and deployment IDs on
TCP 5432. CloudNativePG mode also requires an explicit target `PostgresOperator` network source
and grants that source TCP 5432 and 8000. Missing operator identity or a failed policy write
blocks reconciliation with a retryable status; Pole does not open an entire platform namespace.

## Persistence and reconciliation

Mongo persistence and outbox events remain feature-owned. Kubernetes reconciliation routes every
operation through the deployment's persisted target, namespace, storage, and compute snapshot.
PostgreSQL has no separate credential collection or Kubernetes Secret writer. The Secrets feature
owns encrypted immutable versions, explicit bindings, per-deployment runtime projections and their
cleanup. PostgreSQL calls its provider-owned contracts and retains only transient credential values
when needed for the authorized connection-string operation or operator bootstrap username.

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
its Kubernetes manager never receives a password or creates/deletes Secrets. StatefulSet reconciliation
reads no plaintext values. Operator reconciliation asks Secrets for only the bootstrap username after
projection readiness, immediately before workload apply. A missing projection
returns a retryable deployment failure. Each projection has its own desired/applied version and
failure status in Secrets; a Kubernetes Secret remains a runtime copy, not the authoritative value.

The password endpoint reads the managed version and prepares a replacement preserving the username.
Rotation and the ResourceDeployment apply request commit in one Mongo transaction with an expected
Secret version. Operator-mode rotation remains supported. StatefulSet-mode rotation is rejected:
changing a native Secret does not update the password in an already initialized PostgreSQL database.
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
