# PostgreSQL clusters

The PostgreSQL feature owns tenant-scoped logical database clusters and supports two deployment
modes: a native Kubernetes StatefulSet and the CloudNativePG operator.

## Workload model

`PostgresCluster` stores the logical database configuration, version, topology, storage choice,
and credential state. Kubernetes placement and its resolved infrastructure configuration are
stored in `ResourceDeployment`.

Create resolves the default or requested `ComputeProfile` for the active tenant and target. The
deployment stores the resulting CPU, memory, extended-resource, and scheduling snapshot. Update
without a profile ID preserves that snapshot; an explicit ID adopts the latest eligible profile
revision. Delete and password rotation fence their mutations against the current snapshot.

Quota admission measures the resolved per-instance CPU and memory requests across the requested
instance count. Native StatefulSet deployments support the complete scheduling snapshot.
CloudNativePG supports resources, node selection, affinity, and tolerations; because its Cluster
API does not support runtime class, an incompatible operator-mode request is rejected explicitly.

## Persistence and reconciliation

Mongo persistence and outbox events remain feature-owned. Kubernetes reconciliation routes every
operation through the deployment's persisted target, namespace, storage, and compute snapshot.
Credentials are encrypted at rest and password rotation keeps the logical resource, deployment
generation, and durable reconciliation request consistent.

Related documentation:

- [Compute profiles](../ComputeProfiles/README.md)
- [Workload placement](../../../docs/workload-placement.md)
- [Tenant quotas](../../../docs/quotas.md)
