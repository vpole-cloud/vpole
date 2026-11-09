# Repository GitOps

Pole synchronizes tenant-owned GitHub repository sources through thirteen providers:
ResourceGroup, IsolationScope, Secret, SecretBinding, WebService, PostgresCluster, RedisService,
FunctionApp, ResourceConnection, DomainZone, HostnameBinding, SourceRepository and SourceBuildConfiguration. The durable worker, polling, signed push webhooks and dependency recovery use
phase-aware managed configuration planning. Every poll and Sync checks drift, including unchanged
commits, repairs mutable configuration and records verified no-op without business revisions,
resource events or releases.

The [canonical repository contract](docs/repository-contracts.md) contains the thirteen-kind
coverage matrix, supported YAML schemas and examples, identity rules, catalog policy and phase
semantics. The [authoring examples](examples/README.md) provide minimal, complete thirteen-kind
and isolated lifecycle snapshots within the same #88/#98 PR.

## Compilation boundary

`GitOps.Compiler` owns YAML parsing, strict envelope validation, provider-driven normalization,
canonical hashes, and dependency ordering. Its models, schema limits and provider registry are
independent of Application and Domain. `GitOps.Compiler.Tests` runs compiler checks without loading
Application. HTTP limits, receipt bounds, provider DI registration and durable execution remain in
Application. Concrete resource providers still contribute through GitOps-owned Contracts.

## Execution

`POST /api/v1/gitops/sources/` atomically stores source configuration, the first requested
generation and an outbox signal. A consumer dispatches a Hangfire job containing only tenant/source
IDs. The Mongo recovery scanner also discovers pending requests, delayed runs and expired claims.
An unavailable enqueue cannot lose the request.

Waiting cleanup and retry transitions atomically store an outbox wake-up with the run's persisted
next-attempt time. The consumer schedules that attempt in Hangfire instead of waiting for the
recovery scan. Duplicate and stale jobs still pass the Mongo due-time, lease and generation fences;
the scanner remains the fallback for lost dispatches.

`RepositorySourceJob` claims a run before network access. It checks current tenant/connection
access, resolves the branch to a full SHA, persists it, reads that exact commit and compiles the
whole snapshot before resource writes. Fetch/compile failures remain inspectable runs and never
become an empty desired state. A valid existing directory without YAML is an empty snapshot;
Retain marks removed declarations pending removal. Prune may delete unprotected resources only through
an exact server-owned plan; protected data, an empty desired set and ambiguous create/removal require
explicit preview confirmation.

Accepted input stores normalized declarations, allocated resource IDs and ordered operations.
Internal references use allocated IDs; successful external resolution is pinned durably before
provider mutation. Recreated names never silently change the accepted target. Execution uses the
existing Prepare/Bind/Configure ordering. Workload providers contribute Prepare and Configure;
SecretBinding contributes Bind for its consumer. Prepared workloads remain inert until complete
configuration can activate them. Runtime readiness and configuration applied are separate facts.

Every provider transaction performs real fencing writes against source revision/fence, current
run and worker/attempt/expiry. Resource mutation, ownership, receipt, release and outbox commit
atomically. Successful receipts prevent replayed side effects after a crash. Finalization checks
successful matching receipts for every accepted operation. Lease predicates and expiry use Mongo
time; local scheduling uses `TimeProvider`.

Source creators and Sync/Retry users remain audit metadata. The worker has server authority;
removing an author's membership does not remove tenant-owned automation authority. WebService
releases record source/run/commit provenance without fabricated user IDs or email addresses.

## Named catalogs and YAML

DeploymentTarget, ComputeProfile and StorageProfile creation require an immutable `technicalName`
separate from mutable `displayName`. Names are lowercase DNS labels beginning with a letter,
up to 63 characters, globally unique within their platform catalog. Use the existing admin create
endpoints: `/api/v1/platform/deployment-targets/`, `/api/v1/platform/compute-profiles/` and
`/api/v1/platform/storage/profiles`. Updates cannot rename an entry; concurrent duplicates return
an explicit conflict. No naming backfill or compatibility assignment exists.

Every workload selects one target by technical name and a ComputeProfile by name and explicit
revision. Redis/PostgreSQL also select StorageProfile name/revision and `storage.sizeGb`.
Profiles retain immutable history. Pinned history supplies configuration; the current catalog
still controls enablement, tenant/type access, target availability and size policy. Storage-class
discovery errors remain typed failures. Revoked access cannot be bypassed by an old revision.
See [catalog and identity rules](docs/repository-contracts.md#catalog-and-placement-rules).

Additional IsolationScopes have tenant-wide technical names. `placement.isolationScope` selects
one for a workload; Secret uses `spec.isolationScope`. Omission selects the group's system default
scope. The name `default` is reserved and cannot be declared. Accepted resource/reference IDs stay
pinned; reusing a name never silently rebinds an existing declaration.

The [complete repository example](examples/README.md) includes all thirteen kinds and documents
its catalog, GitHub, image and Secret-value prerequisites. WebService supports Private/Public exposure and either fixed replicas or autoscaling.
SecretBinding uses `consumer: { kind, name }`: Runtime grants support WebService/FunctionApp;
Bootstrap grants authorize PostgreSQL initialization without a runtime projection. YAML never
contains Secret values, credentials, tenant authority or internal Pole IDs.

Git-owned resources reject manual configuration changes and deletion in the existing API/UI.
Supported operational actions and PostgreSQL credential rotation retain their ordinary workflows.
Group/scope/target changes, immutable storage changes and disappeared created resources produce
blockers; they do not cause implicit adoption, relocation or recreation.

## Connectivity, domains and source delivery

ResourceConnection pins both endpoint resource IDs and the published endpoint selector. Its Bind
phase follows source preparation and destination configuration; full preflight rejects phase cycles.
WebService connection environment references select Host, Port or a published Configuration key,
without credentials. Runtime network-policy observations stay outside managed projections.

DomainZone and HostnameBinding have tenant-unique technical names distinct from DNS labels and
relative names. Generated identities stay explicit system identities. A binding may attach to a
Public WebService owned manually or by another source without transferring workload ownership.
Attachment repairs use the complete binding set, preserve aliases across WebService updates and
record the binding source's GitOps provenance. Custom bindings prevent a Private exposure transition.
Awaiting-artifact attachments retain reservations and desired ingress without activating routes.

SourceRepository stores connection, external repository and branch identity without a selected
analyzer candidate. Analyzer results are optional authoring aids. SourceBuildConfiguration stores an
explicit inline Dockerfile; updating it creates a configuration revision and never queues a build.
WebService image authority is either a concrete image or a pinned build configuration. The latter
accepts full configuration as `AwaitingFirstArtifact`, with an inert prepared placement.

Manual Build resolves the current application branch once and fixes its SHA, recipe, configuration
revision and generation in the job. Crash recovery retains that input and uses a leased monotonic
attempt. Current tenant/connection access, repository/configuration versions, generation and lease
are checked before transactional artifact promotion. The verified digest, placement activation,
release, receipt and outbox commit together; exact replay creates no second release. A GitOps poll
compares image authority, not the delivered digest, so it preserves the last artifact. The application
commit and manifest commit are separate provenance facts. A configuration still bound to a live
WebService cannot be deleted before a supported lifecycle transition.

## Source API

All source routes use bearer authentication and `X-Pole-Tenant-ID`. Viewer can read;
Member/Admin/Owner can write, with existing platform-admin behavior preserved. Foreign sources
and runs return not-found. Tenant/user authority comes from the server, never the body.

Under `/api/v1/gitops/sources`:

| Method/path                                            | Input and behavior                                                                                                                                                                               |
| ------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `POST /`                                               | `idempotencyKey`, `provider`, `connectionId`, `externalRepositoryId`, `branch`, `manifestDirectory`, optional `pollIntervalSeconds` and `prune` (Retain by default); returns the created source. |
| `GET /`, `GET /{id}`                                   | Configuration, generations, commit markers, current/latest run identity, sanitized latest-run diagnostics and inventory counts. Lists use bounded `page`/`pageSize`.                             |
| `PUT /{id}`                                            | `expectedRevision`, `pollIntervalSeconds`, `prune`; repository location changes through preview only.                                                                                            |
| `POST /{id}/sync`                                      | `expectedRevision`, `idempotencyKey`; returns 202 for a durable request to resolve the current branch.                                                                                           |
| `POST /{id}/retry`                                     | `expectedRevision`, `idempotencyKey`, `runId`; returns 202 for a new run using the pinned input.                                                                                                 |
| `POST /{id}/pause`, `POST /{id}/resume`                | `expectedRevision`; pause fences current execution and retains ownership; resume schedules a fresh Sync.                                                                                         |
| `POST /{id}/previews`                                  | `intent`, `expectedRevision` and selectors; returns the server-owned action graph.                                                                                                               |
| `GET /{id}/previews`, `GET /{id}/previews/{previewId}` | Bounded sanitized preview reads; detail paginates actions.                                                                                                                                       |
| `POST /{id}/previews/{previewId}/confirm`              | Only `expectedRevision` of the preview; returns 202 and the same run on replay.                                                                                                                  |
| `DELETE /{id}?expectedRevision=…`                      | Requires empty ownership and no pending work; preserves an audit/idempotency tombstone.                                                                                                          |
| `GET /{id}/resources`                                  | Bounded source inventory metadata with an inventory revision; no managed projections or values.                                                                                                  |
| `GET /{id}/adoption-candidates`                        | Tenant-scoped kind/name lookup; unnamed binding discovery uses Secret and consumer metadata through its owner.                                                                                   |
| `GET /{id}/runs`, `GET /{id}/runs/{runId}`             | Paginated history and run details.                                                                                                                                                               |
| `GET /{id}/runs/{runId}/operations`                    | Bounded pending/succeeded/blocked descriptors and sanitized diagnostic codes.                                                                                                                    |

Repeated idempotency keys with identical input do not create another source/generation; changed
input conflicts. Retry requires the latest failed/blocked run at the same configuration revision,
a pinned SHA, and no active or pending work. If the run accepted a snapshot, Retry retains it.
Without a SHA, request Sync. New signals during a run coalesce into a later generation and never
change its commit. A retry reserves its run before later Sync requests can be claimed.

Observed commit advances after resolution, attempted commit after acceptance, fully applied commit
only after all phases succeed. Completed generation includes failures/blockers; it is not a success
flag. For an unchanged commit/revision, the worker may reuse the latest successful accepted snapshot
without downloading it again. It still rechecks current access and replans every operation. A later
failed or partially applied run prevents this reuse optimization.

The former manual snapshot/resume routes are removed. There is no public YAML apply API.
Responses do not expose raw manifests, accepted specs, credentials or Secret values.

## Console workflow

The tenant-scoped **GitOps** workspace owns source catalog/create and source tabs for Overview,
Resources, Runs, Previews and Settings. GitHub connections remain in Integrations. Source creation
selects a connection, repository and branch directly, without analyzer or SourceRepository creation.
Settings changes polling/prune with expected revision; relocation uses a reviewed destination.

Visible active execution polls every five seconds; idle sources poll every twenty seconds. Hidden
pages and inactive tabs do not poll, and slow reads coalesce. The query scope includes authenticated
session, API, tenant and complete route/page parameters. Drafts and confirmations remain local;
workspace persistence contains only references. Current credentials are acquired for every request.

Resources exposes reserved/previously-created identities, pending removal, last run and sanitized
blockers. Adoption lookup returns concrete identities and ownership metadata; compatibility remains
an owner preview decision. For an unnamed manual SecretBinding, choose Secret and consumer, inspect
usage/target metadata and provide the desired declaration name from Git.

Preview input identifies inventory-only detach or the immutable repository input, including a saved
relocation destination. Its summary includes counts and warnings for the whole plan. Preview counts
are actions; run summaries count phase operations. The console loads every preview action page and
checks consistent preview identity, revision and total before enabling Confirm. Plans with possible
data loss require an acknowledgment tied to that exact preview revision. A known source/inventory
revision change or stale confirmation requires a new reviewed preview.

Confirmation sends only preview identity/revision and follows the server-owned durable run. An
accepted write whose refresh fails retries the read. Unknown write results can repeat only their
captured input and idempotency key or preview identity. Tenant/account/permission changes fence old
callbacks and clear confirmations. Viewers remain read-only. Git ownership notices navigate to the
managing source; source UI never exposes Secret values, credentials, accepted specs, YAML or Dockerfile.
Offline detach remains available when repository access fails.

## Scheduling and GitHub webhook

Source polling defaults to five minutes and has a one-minute minimum. `GitOpsWorker` server
options default to a one-minute scan, batch 100, two-minute lease, 20-second heartbeat and five
consecutive failed attempts. Temporary failures persist delays of 1/2/4/5 minutes; exhaustion
finishes the run with an explicit failure. Polling or Sync can start a new run later.

`GitOpsWorker:CleanupCheckSeconds` defaults to 5 seconds and accepts 1–300. Waiting-cleanup
resumption uses this separate cadence; `HeartbeatSeconds` continues to renew the lease.
Each wait atomically persists its due time and outbox wake-up, and scheduled execution retains
the server-clock due, claim and generation fences. The minutely recovery scanner remains the
fallback for lost dispatches. The genuine-failure retry delays above are unchanged.

`Hangfire:SchedulePollingIntervalSeconds` defaults to 5 and accepts 1–60. This controls discovery
of due scheduled jobs; it does not change the recurring recovery scanner or source poll interval.

Configure a separate `GitHubWebhook:Secret` (required at startup when GitHub is enabled), and point
the GitHub App push webhook at `POST /api/v1/integrations/github/webhook`. Use a randomly generated
secret. The endpoint verifies HMAC-SHA256 over the raw bytes with constant-time comparison before
parsing, and rejects bodies above 25 MiB, including streamed bodies without Content-Length.

A delivery receipt stores only its GUID, payload hash, event type and receive time. Receipt and
GitProviders-owned outbox event are atomic. Redelivery is idempotent; a reused ID with different
content conflicts. Signed ping/irrelevant events are acknowledged without GitOps work. Installation
identity resolves the stored connection/tenant; payload tenant fields are ignored. The consumer
calls the GitOps scheduling contract using provider/connection/repository/branch. The SHA is only
a hint: the worker resolves the current branch. Deleted branches fail with ref-not-found and
preserve inventory. Polling independently recovers missed deliveries.

## Secret values and recovery

YAML declares Secret metadata and keys, never values. Supply values through the existing protected
Secret API/UI. The Secrets-owned `SecretChangedEvent` adapter forwards value rotations through the
generic GitOps dependency wake contract. Eligible blocked runs reserve an accepted-input retry;
replayed signals are idempotent and do not displace newer pending Sync work. GitOps does not import
Secrets Events. PostgreSQL consumes its bootstrap grant once through the Secrets-owned encrypted
credential workflow; later source-value rotations do not reset the database password. FunctionApp
and WebService use logical Secret/key references. Secrets resolves native names/keys at provisioning,
and old references remain pinned until the owning workload confirms the previous rollout has completed.
`ConfigurationApplied` alone does not release those pins.

## Persistence and lifecycle

`RepositorySource`, `GitSynchronizationState` and `GitReconciliationRun` replace the manual source
and execution collections. `ManagedDeclaration` remains ownership inventory, `GitOperationReceipt`
remains operation truth and `GitAcceptedReference` retains resolved identities. All stored models
belong to Domain with BSON attributes; Application owns filters, transactions and boundary mappings.
There are no compatibility readers, backfills or dual writes: Pole has not been deployed.

GitOps stores revisioned operation plans and desired/applied projections in the existing inventory.
Allocated IDs may be created; a missing previously created resource or changed identity blocks.
Accepted references remain pinned. Preconditions use owner-provided transactional reads and real
fences. Three immediate conflicts fall back to persisted recovery; API summaries count operations
and expose actions and field paths without projection values. WebService Prepare does not mark the
full configuration applied; Configure verifies managed fields while preserving connection references
and server credentials.

The lifecycle API implements pause/resume, guarded deletion, adoption, prune, explicit replacement/rename,
repository relocation and terminal detach for all thirteen providers. Detach uses an inventory-only
preview and works before any successful Git fetch. Confirmation is the sole durable entrypoint for
reviewed intents; there is no separate detach command. Pause retains manual-write guards, while
detach returns control after the complete inventory is released.

Deletion retains ownership through asynchronous cleanup. Receipt-proven own transitions can advance
the approved version vector; unrelated changes stop the unexecuted remainder. Replacement uses the
owner's delete/create workflows and can lose data or cause downtime. It does not copy Secret values,
reset adopted PostgreSQL credentials or promise distributed rollback.

The GitOps console uses the same source/run/preview APIs and scoped visible polling. Complete
authoring examples are maintained beside this feature. Live Kubernetes drift and runtime readiness
remain the runtime reconcilers' responsibility.

Feature verification requires solution build; affected GitOps, Secrets, PostgreSQL, Redis,
Functions, GitProviders, SourceBuilds and architecture suites; full shared Integration; frontend
test/build/lint when frontend transports change; GitHub setup, repository/source-build/lifecycle
Aspire E2E; and live workload, PVC/credential, ingress and network cleanup checks. The
[E2E guide](../../../docs/aspire-e2e-testing.md#gitops-repository-scenarios) separates the mixed
configuration scenario from the real SDK Function activation/Secret rollout test. These describe
verification boundaries, not a record that pending runs passed. The complete unfiltered publication
gate runs after all stages before the single PR; stage evidence does not replace it.
