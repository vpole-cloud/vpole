# PostgreSQL clusters

The PostgreSQL feature owns tenant-scoped logical database clusters and supports two deployment
modes: a native Kubernetes StatefulSet and the CloudNativePG operator.

Placement is stored in `ResourceDeployment`. Create resolves the default or requested
`ComputeProfile`; update without a profile ID preserves the current compute snapshot, while an
explicit ID adopts the latest matching revision. Delete and password rotation fence their
mutations against the current deployment snapshot.

CPU, memory, extended resources, and scheduling no longer belong to `PostgresClusterSpec`.
Quota admission uses the resolved CPU/memory requests per instance multiplied by the requested
instance count exactly once. The native StatefulSet supports the full scheduling snapshot.
CloudNativePG supports resources, node selection, affinity, and tolerations, but not runtime
class; operator-mode requests using a runtime-class profile fail validation explicitly.

Mongo persistence and outbox events remain feature-owned. Kubernetes reconciliation routes every
operation through the deployment's persisted target and namespace.

Related documentation:

- [Compute profiles](../ComputeProfiles/README.md)
- [Workload placement](../../../docs/workload-placement.md)
- [Tenant quotas](../../../docs/quotas.md)
