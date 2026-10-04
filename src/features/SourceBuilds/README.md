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

## Retained build output

Source Builds owns a separate `SourceBuildLogs` capture for each claimed job. Capture state is
independent of job status and target apply: `Completed` means the capture closed, including a failed
build, and never establishes deployment success or workload readiness. Archives, recipes, command
arguments, environment maps, Docker config and raw infrastructure exceptions are not emitted by the
capture. Fixed system messages and BuildKit stdout/stderr are saved while the process runs.

`SourceBuildLogs` configuration defaults to `MaxBytes=1048576`, `MaxEntries=2048`,
`MaxEntryBytes=4096` and `RetentionDays=7`. Supported ranges are 4096–4194304 bytes,
16–4096 entries, 256–8192 bytes per entry (within the total cap), and 1–30 days. Limits are validated
at startup and snapshotted per capture. The retained UTF-8 prefix stops growing at either size or
entry count; `Truncated` is explicit and both process pipes continue draining.

The worker renews a two-minute writer lease every thirty seconds. Single-document CAS writes fence
tenant, WebService, job, writer, revision and lease, keep monotonically ordered sequence ranges, and
accept identical duplicate appends without duplication. Ordering reflects serialized capture arrival,
not an exact interleaving of two OS pipes. Recovery runs every minute in batches of at most 100,
seals expired leases as `Interrupted`, and clears expired output while retaining cursor metadata.
Reads enforce retention immediately, even before cleanup. Restart preserves a saved prefix; an
interrupted capture does not automatically resume the existing build job. Storage failure leaves
an explicit unavailable/interrupted prefix and does not change build outcome or block pipe drainage.

Tenant readers use `GET /api/v1/source-build-jobs/{id}/logs?webServiceId={id}&afterSequence={n}`.
The persisted job verifies resource ownership, so history remains readable after configuration or
WebService deletion. A response contains at most 32 entries and 64 KiB of text, stable next/last
sequences, capture state, retention and truncation. Ahead cursors return 400; callers never silently
reset them. Browser cancellation affects that read alone, while Builds and Operations share one
plain-text viewer with follow/pause, cursor reconnect and a bounded 256 KiB/512-entry tail. A build
deep link can read an older exact job outside the recent-build page.

Per-invocation redaction runs before persistence and handles known registry username/secret,
combined credentials, common Base64, URL and JSON forms, including values split across reads.
Sanitizer carry is bounded; oversized known inputs disable raw capture with an explicit message.
This is not perfect sanitization of arbitrary application output or arbitrary encodings. Application
logs can contain other sensitive data, and authorized readers should treat captured output accordingly.

## Validation

`SourceBuildDeletionTests` covers active-build rejection with rollback, deletion-window admission,
final deletion/outbox, duplicate cleanup, tenant isolation, retained history, and concurrent
queue/deletion. Existing SourceBuild API and WebService lifecycle tests exercise HTTP and
finalization boundaries. These integration tests require the Mongo replica-set fixture.
