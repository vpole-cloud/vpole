# Source Builds

Source Builds owns build configurations and immutable-input jobs. Configuration and job APIs are
scoped by `X-Pole-Tenant-ID`; job history can be filtered by configuration or WebService ID even
after those resources are removed. Manual builds currently provide the queue entrypoint.

## Integration boundary

Source Builds owns the contribution contracts used by target adapters. WebServices implements
`AdmitSourceBuildWebService` to fence its own active target inside a Source Builds transaction,
and adapts its deletion event to `CleanupSourceBuildConfigurations`. The cleanup capability and
Mongo filters remain Source Builds-owned; the adapter never reads Source Builds collections.
This follows the platform contribution pattern without coupling Source Builds to target features.
Configuration creation and queueing rely on transactional admission rather than an earlier
existence check that can become stale before the write.

## WebService deletion

Deleting a WebService rejects queued, running, or deployment-waiting builds with HTTP 409
(`source-builds.web-service-has-active-jobs`). Failed and succeeded jobs do not block deletion.
An inactive configuration can remain in place: users need not remove it before deleting the service.

The deletion transaction writes the WebService lifecycle first, then checks tenant-scoped active
jobs before requesting deployment deletion. A rejection rolls back lifecycle, quota, and outbox
changes. Build admission writes the same active WebService document in the job/outbox transaction.
This serializes admission against deletion; its persistence `Revision` advances, while desired
`ActiveRevision` stays unchanged. Concurrent stale mutations can return the usual update conflict
and be retried. Configuration creation uses the same fence to avoid orphan configurations.

Once deleting, the service rejects build admission even while its configuration still exists.
After deployment cleanup, the existing finalizer deletes the service and writes
`WebServiceDeletedEvent` atomically through the outbox. A WebServices event consumer calls the Source Builds-owned cleanup contribution, which deletes only
configurations matching the event tenant and service ID. Duplicate delivery is harmless. Jobs are
never deleted by this consumer, so completed build history remains queryable.

## Validation

`SourceBuildDeletionTests` covers active-build rejection with rollback, deletion-window admission,
final deletion/outbox, duplicate cleanup, tenant isolation, retained history, and concurrent
queue/deletion. Existing SourceBuild API and WebService lifecycle tests exercise HTTP and
finalization boundaries. These integration tests require the Mongo replica-set fixture.
