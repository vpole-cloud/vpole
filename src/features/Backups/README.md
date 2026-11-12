# Service backups

Backups protects tenant workloads. A service provider implements execution, catalog access and recovery for its resource type. The PostgreSQL provider uses CloudNativePG and the Barman Cloud plugin. Its destination is an existing S3-compatible bucket controlled by the tenant.

## Configure a destination and a policy

Create a tenant Secret containing the S3 access key ID and secret access key. Use the same resource group and isolation scope as the source service. Configure a destination with the HTTP(S) endpoint origin, bucket, prefix, Secret ID and the two key names. The endpoint has to be reachable from the control plane for catalog reads and from the workload target for backups and recovery. Object storage can be on a shared private network. An endpoint reachable only inside the workload cluster is covered by [#113](https://github.com/lsbV/Pole/issues/113).

Creating the destination through the API binds the selected keys to that destination with `BackupStorage` usage in the same transaction. For GitOps, declare a `SecretBinding` with `usage: BackupStorage`, the matching `BackupDestination` consumer, and mappings for its credential keys. The purpose-specific grant permits the control plane to use these keys for that destination. Workload access uses a Secrets-managed Kubernetes projection on the source target. Both paths resolve the currently authorized Secret version. Rotate values through the existing Secret version operation. A failed catalog read, revoked grant or unreadable metadata produces an error on the affected source. A successful empty list means no completed copies were found.

Create one policy per source service, selecting an authorized destination, a five-field UTC cron schedule and the provider's retention setting. PostgreSQL expresses retention in days. CNPG runs scheduled base backups even during Pole downtime. Pausing the schedule leaves WAL archiving, manual copies and active retention running. Setting a new destination on the policy creates a separate archive; previously completed copies remain selectable through their original archive. After source deletion or destination replacement the owner manages cleanup of retired archives.

Day fields follow Pole's shared cron semantics. A field covering every possible day is unrestricted;
when both day-of-month and day-of-week are restricted, either match schedules the run. The PostgreSQL
provider expands these fields and normalizes Sunday to `0` before adding CNPG's seconds field. This
preserves the same schedule despite the operator parser's different treatment of wildcard syntax.

## Tenant API

Routes are under `/api/v1/backups` and require an authenticated tenant selection through `X-Pole-Tenant-ID`.

| Method           | Route                             | Result                                                                                                           |
| ---------------- | --------------------------------- | ---------------------------------------------------------------------------------------------------------------- |
| GET, POST        | `/destinations`                   | List or connect destinations                                                                                     |
| GET, PUT, DELETE | `/destinations/{id}`              | Inspect a destination or apply a revision-checked request. Use a new destination to change its storage identity. |
| GET, POST        | `/policies`                       | List or create policies                                                                                          |
| GET, PUT, DELETE | `/policies/{id}`                  | Inspect or update a policy, or stop it with a revision precondition                                              |
| GET              | `/archives`                       | Discover current and retained archive identities for the selected tenant                                         |
| DELETE           | `/archives/{id}?confirmed=true`   | Forget a retired archive and release its credential placement                                                    |
| GET              | `/archives/{id}/backups`          | Fetch completed provider backup IDs and completion times directly from the catalog                               |
| POST             | `/policies/{id}/runs`             | Request a manual backup                                                                                          |
| POST             | `/archives/{id}/restores`         | Confirm a restore from a selected copy or to a timestamp                                                         |
| GET              | `/operations`, `/operations/{id}` | Inspect durable execution history and progress                                                                   |

Writes to existing configuration include `expectedRevision`, in the query string for deletes. Manual backup and restore commands include an `idempotencyKey`. Retrying the same command returns its original operation. Reusing a key with different parameters produces a conflict. Runtime history and manual operations do not participate in GitOps drift detection. The console distinguishes last successful base-backup time, WAL archiving health and the next scheduled execution.
When an applied schedule has no future timestamp from the operator, Pole calculates its expected next run
from the UTC cron expression. Retiring an archive marks still-running scheduled history entries as
`Interrupted` when final operator status was unobserved. The live catalog remains authoritative for completed copies.

## PostgreSQL restore

An Owner or Admin of the selected tenant, or a platform administrator acting in that tenant, confirms the archive, target and new resource name. Supply exactly one of `backupId` and `targetTime`. A backup ID restores the consistent state at the end of that specific copy. A timestamp includes an explicit UTC offset and supports microsecond precision. The provider checks for a suitable base backup before requesting WAL replay to the selected time. An absent WAL segment or an unreachable target is a failed restore. Recovery succeeds after the new primary reaches the target and is ready.

Restore creates a separate PostgreSQL resource on the source DeploymentTarget, subject to placement, storage and quota admission. Its single owner connection string uses the SQL superuser `postgres` and database `postgres`, with a fresh managed password. It also recovers customer databases and roles. The recovered resource starts under manual ownership and can be explicitly adopted by GitOps later.

Retained archive access uses its destination registration, deployment placement and authorized storage credentials. With this metadata intact, the original PostgreSQL workload and its administrator Secret can be deleted before listing copies or restoring. Deleting a policy preserves the archive and its customer-owned files. The backup list reads metadata on demand and has no persisted duplicate in Pole.

CloudNativePG, the Barman Cloud plugin and their CRDs must be installed on the selected target. The lab exercises the supported combination against S3-compatible storage. [Barman Cloud plugin](https://cloudnative-pg.io/plugin-barman-cloud/docs/usage/) · [CNPG recovery](https://cloudnative-pg.io/docs/1.30/recovery/)

An Owner or Admin can forget an inactive archive by sending
`DELETE /api/v1/backups/archives/{id}?confirmed=true` after its policy reconciles and previously submitted
operations finish. Pole removes the catalog and restore entry and releases the credential placement when
no other archive uses it. S3 objects remain at the original endpoint under the owner's control.

Restore requests can set `storageGiB` to provision a larger volume. The archive response includes the
last observed source volume size, which is the minimum allowed size. This is useful when the source
grew between Pole observations. The selected storage profile's limits and quotas apply.

CloudNativePG 1.30 evaluates `ScheduledBackup` in the operator's local time zone. Run the operator
in UTC (the stock image default) to execute Pole's UTC schedules.
