# Service backups

Backups protects tenant workloads. A service provider implements execution, catalog access and recovery for its resource type. The PostgreSQL provider uses CloudNativePG and the Barman Cloud plugin. Its destination is an existing S3-compatible bucket controlled by the tenant.

## Configure a destination and a policy

Create a tenant Secret containing the S3 access key ID and secret access key. Use the same resource group and isolation scope as the source service. Configure a destination with the HTTP(S) endpoint origin, bucket, prefix, Secret ID and the two key names. The endpoint has to be reachable from the control plane for catalog reads and from the workload target for backups and recovery. Object storage can be on a shared private network. An endpoint reachable only inside the workload cluster is covered by [#113](https://github.com/vpole-cloud/vpole/issues/113).

Creating the destination through the API binds the selected keys to that destination with `BackupStorage` usage in the same transaction. For GitOps, declare a `SecretBinding` with `usage: BackupStorage`, the matching `BackupDestination` consumer, and mappings for its credential keys. The purpose-specific grant permits the control plane to use these keys for that destination. Workload access uses a Secrets-managed Kubernetes projection on the source target. Both paths resolve the currently authorized Secret version. Rotate values through the existing Secret version operation. A failed catalog read, revoked grant or unreadable metadata produces an error on the affected source. A successful empty list means no completed copies were found.

Workload credential rotation is asynchronous: Secret projection and Barman's short credential cache
must converge. WAL status reflects the new credentials after a subsequent archive attempt.

Create one policy per source service, selecting an authorized destination, a five-field UTC cron schedule and the provider's retention setting. PostgreSQL expresses retention in days. CNPG runs scheduled base backups even during Pole downtime. Pausing the schedule leaves WAL archiving, manual copies and active retention running. Setting a new destination on the policy creates a separate archive; previously completed copies remain selectable through their original archive. After source deletion or destination replacement the owner manages cleanup of retired archives.

Day fields follow Pole's shared cron semantics. A field covering every possible day is unrestricted;
when both day-of-month and day-of-week are restricted, either match schedules the run. The PostgreSQL
provider expands these fields and normalizes Sunday to `0` before adding CNPG's seconds field. This
preserves the same schedule despite the operator parser's different treatment of wildcard syntax.

## Durable execution

Policy changes and manual commands commit their work request to the transactional outbox with
the document mutation. Consumers run the tenant-scoped workflow after commit. A continuation
commits its next attempt and another work request together; its consumer schedules a Hangfire
job when the attempt is in the future. The jobs call the same claimed workflow.

A minutely Hangfire recovery job requests at most 100 due policies, 100 due operations and 100 due archive observations,
including expired claims. It recovers lost delivery or enqueue without a polling hosted service.
Duplicate delivery uses the same operation-derived native identity and restore creation receipt.
Completed operations cannot claim execution again. Stored trace and correlation metadata follow
consumer delivery, delayed jobs and recovery. Diagnostics include work and tenant identity plus
the exception type and stack, excluding native response bodies and credential values.

CNPG and Barman continue to own the physical schedule, WAL archiving and active-archive retention.

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

Writes to existing configuration include `expectedRevision`, in the query string for deletes. Manual backup and restore commands include an `idempotencyKey`. Retrying the same command returns its original operation. Reusing a key with different parameters produces a conflict. Runtime history and manual operations do not participate in GitOps drift detection.
The operations list returns `items` and `nextCursor`, ordered newest first by creation time and ID.
Use `after` for the next page and `pageSize` (default50, maximum200). Each page remains tenant-scoped;
new operations do not shift older pages. The console distinguishes last successful base-backup time, WAL archiving health and the next scheduled execution.
When an applied schedule is currently observed as enabled but has no future operator timestamp,
Pole calculates its expected next run from UTC cron. A native readiness/health pause, failed observation
or interrupted claim leaves the next run empty while retaining the historical applied revision. Native scheduled-backup history is separate from this health read:
each observation consumes at most 200 Kubernetes objects/events and fifteen seconds. It saves a paged
LIST checkpoint, then follows WATCH changes using the opaque resource version. Expired versions start
a new backfill; accepted history, checkpoint and continuation commit together. Backfill finalization
and source removal interrupt missing running history in bounded batches. A temporary native outage
keeps observation retryable. The live S3 catalog remains authoritative for completed copies.

Changing destination or deleting a policy first suspends the old native schedule. Pole keeps the old
WAL plugin attached while a post-suspension snapshot/WATCH still reports physical base backups or
manual commands remain unfinished. The exact archive, policy revision and publisher fence authorize
the later plugin switch/removal. Ordinary retirement continues observing already submitted copies.
Forgetting archive metadata requires the committed quiescence receipt and finished commands; it never
lists the whole native history synchronously.

This is bounded observation of the operator. CNPG can already have passed its suspension check and
publish a child Backup after Pole's snapshot. Configuration changes use an asynchronous handoff;
the receipt describes the observed native set rather than a transactional scheduler acknowledgement.
Catalog availability and the verified outcome of a restore remain separate signals.

## PostgreSQL restore

An Owner or Admin of the selected tenant, or a platform administrator acting in that tenant, confirms the archive, target and new resource name. Supply exactly one of `backupId` and `targetTime`. A backup ID restores the consistent state at the end of that specific copy. A timestamp includes an explicit UTC offset and supports microsecond precision. The provider checks for a suitable base backup before requesting WAL replay to the selected time. An absent WAL segment or an unreachable target is a failed restore. Recovery succeeds after the new primary reaches the target and is ready.

Restore creates a separate PostgreSQL resource on the source DeploymentTarget, subject to placement, storage and quota admission. Its single owner connection string uses the SQL superuser `postgres` and database `postgres`, with a fresh managed password. It also recovers customer databases and roles. The recovered resource starts under manual ownership and can be explicitly adopted by GitOps later
through preview → adopt without replacing its managed password or recovery origin. Restore re-resolves
the current revisions of the same compute/storage profiles on the retained target. A profile rename
is allowed; current incompatibility, access, capacity or quota restrictions reject the request.

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

## GitOps and request examples

`BackupDestination` and `BackupPolicy` are independent declarations. A policy resolves its service and
destination by tenant identity and can be owned by another source than the service. The
[backup snapshot](../GitOps/examples/backups/manifests) declares storage-key metadata, the explicit
`BackupStorage` grant, destination and policy for an existing PostgreSQL service. Secret values are
supplied outside Git. Sync and no-op verification do not include catalog reads or manual operations.

For an archive ID returned by `GET /api/v1/backups/archives`, send one of these bodies to
`POST /api/v1/backups/archives/{id}/restores` with the selected tenant header:

```json
{
    "idempotencyKey": "restore-reviewed-copy",
    "name": "orders-copy",
    "backupId": "20261113T020000",
    "confirmed": true
}
```

```json
{
    "idempotencyKey": "restore-reviewed-time",
    "name": "orders-at-time",
    "targetTime": "2026-11-13T02:15:00.123456+02:00",
    "confirmed": true
}
```

A successful request returns the durable operation. Follow its ID through `GET /operations/{id}`.
Reuse the same idempotency key after an unknown response for the same reviewed command. An accepted
command followed by a read failure is still accepted. A selected copy can disappear after listing;
missing copies and unreachable times are explicit failures, never substitutions of another target.

Mongo validators freeze the four owned document formats independently of runtime mappings. Execution
variants require their own receipt/backup IDs and claims; PITR time uses BSON document representation
with ticks. Schema validation and index migrations are separate. No catalog copy is stored in Mongo.
