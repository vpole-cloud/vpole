# SeaweedFS

SeaweedFS is a tenant-owned S3 service. Creating an instance provisions its own master, volume server, filer and private S3 gateway on one deployment target in the selected isolation scope. Each instance has an independent bucket namespace and administrator credential.

## Provisioning

The target needs the SeaweedFS operator and its bucket, IAM and maintenance CRDs, a compute profile without a runtime class and a storage profile with a dynamically provisioned Delete-reclaim storage class. Configure the target network source `seaweedfs/operator` with the controller's namespace and pod labels. Replicated deployments with dedicated metadata also need CloudNativePG and the `postgres/operator` network source. The pinned versions, chart digests and isolated acceptance commands are in the [Kubernetes lab guide](../../../infrastructure/kubernetes-lab/seaweedfs.md).

The Pole target credential needs read, create, update and delete access to the managed Seaweed, Bucket, S3Identity, S3Credentials, S3Policy, S3PolicyBinding and AdminScript resources in workload namespaces. It also needs the usual Secret, pod, NetworkPolicy and PVC permissions, plus read access to storage classes and PVs to verify reclaim. Dedicated metadata uses a CNPG Cluster. Existing metadata uses CNPG DatabaseRole and Database objects and a short-lived PostgreSQL connection check.

| Mode                             | Filer metadata                                     | Workload pods | Persistent storage requested for a volume disk of S GiB |
| -------------------------------- | -------------------------------------------------- | ------------: | ------------------------------------------------------: |
| Single node                      | Embedded filer store                               |             4 |                                               S + 2 GiB |
| Replicated, dedicated PostgreSQL | Three synchronous CNPG instances                   |            15 |                                              3S + 6 GiB |
| Replicated, existing PostgreSQL  | A private database and login on a selected cluster | 12 additional |                                   3S + 3 GiB additional |

Single-node mode depends on its worker and disk. Replicated mode spreads each component across three distinct Kubernetes worker hosts, writes two copies of object data and synchronously commits filer metadata. Recovery requires an available synchronous PostgreSQL quorum, persistent disks and sufficient free space to rebuild the lost copies. The table counts physical provisioning. Two-copy data and repair headroom reduce usable S3 capacity.

Replicated mode defaults to a dedicated CNPG cluster. Choose an existing, eligible PostgreSQL cluster on the same target to reuse capacity. Pole checks tenant ownership, availability and synchronous HA when creating the instance. It provisions a database and login for the filer, grants the filers network access and protects the dependency until final purge. The cluster owner retains responsibility for the PostgreSQL service. The metadata choice is fixed for the lifetime of the SeaweedFS instance.

## Buckets and access

Create a bucket on the instance page. Its workload Secret contains `accessKey` and `secretKey`. Bind the Secret to the workload and declare a ResourceConnection to the instance's `s3` endpoint on TCP 8333. Rotation creates a new credential version. Clients using static credentials must pick up the projected version. Managed bucket keys are private to a bucket. The tenant owner can reveal the administrator credential and manage native IAM. Extra native permissions belong to the administrator.

An authorized Private Access grant permits local access using `pole forward seaweedfs/<instance-name>`. Use the forwarded S3 URL with path-style addressing. The grant authorizes network access. S3 authenticates the caller using its own credentials.

## Retention and cleanup

Deleting an instance closes its S3 gateway during reconciliation. Deleting an individual bucket disables its managed identity and applies an explicit deny policy. Pole retains the data, names and disk quota until cleanup succeeds. Privileged operations performed directly on the native service can modify or destroy these data.

`SeaweedFS:DeletionRetention` in Pole.Server configuration defaults to seven days. Pole stores the deadline when deletion is requested. Restore the instance or bucket before that deadline to reopen access. Once purge begins, Pole waits for the operator, storage provisioner and any claimed metadata database to finish cleanup before releasing capacity and the name. The UI exposes the deadline and revision-checked operations. Native bucket reconciliation and cleanup share a per-bucket distributed lock and re-read durable lifecycle intent under that lock, so a delayed reconciliation cannot recreate a purged bucket after its name is released.

The tenant API is rooted at `/api/v1/seaweedfs` and uses `X-Pole-Tenant-ID`. Lists, placement, search and health expose resource metadata. The credential endpoints return values only on an explicit reveal.
