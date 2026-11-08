# Resource deployments

ResourceDeployments owns persisted physical workload placement, reconciliation attempts and
current apply/delete outcomes. Domain stores the deployment state machine; Contracts exposes
provider extension points and tenant-safe placement summaries; Application owns API reads,
transactional state/outbox writes, recovery scheduling and adapter/observer catalogs. Resource
providers own logical configuration and native object selection. RuntimeCells owns namespaces;
DeploymentTargets owns clients and admission. This feature does not store credentials or audit history.


## Initial placements

WebServices and Redis create their initial placements through `Pole.Platform.Resources.InitialResourcePlacements`
in both API and GitOps create flows. The caller supplies the resource display name, the desired placements and its
active Mongo transaction. The platform derives the tenant, group, type and resource ID from those placements,
registers the resource once, then creates its deployment set in the same session. The caller aborts on either
typed failure. Adding a target to an existing resource uses the existing deployment-set contract directly.

## Tenant operations and runtime observation

`GET /resource-deployments/operations` pages current persisted placements in the active tenant,
optionally by resource group or exact resource type and ID. State totals cover the whole filtered
set; pod observations cover the selected page. Waiting, Accepted, Running, Applied and Failed
remain reconciliation phases, with operation/desired generations, attempts, transition time and
retry eligibility. This is current durable state, not an audit history. Closing a browser view
stops observation without cancelling server work. Builds and releases retain their own feature
state and are displayed separately in the WebService Operations section.

`GET /resource-deployments/{deploymentId}/observation` resolves only that tenant-owned persisted
placement. Providers contribute `IResourceDeploymentObserver` through a validated descriptor
catalog; the platform never switches on built-in resource types. WebServices select Deployments,
Redis StatefulSets, and PostgreSQL its configured StatefulSet or CloudNativePG Cluster.
The operator signal combines Ready with provider-owned evidence for the current native generation.
CloudNativePG 1.30 Ready conditions can omit `observedGeneration`, as shown by its
[pinned status writer](https://raw.githubusercontent.com/cloudnative-pg/cloudnative-pg/v1.30.0/pkg/resources/status/transactions.go);
condition presence alone does not establish freshness.
Applied confirms an adapter write, not application health. Ready requires the current Pole and
native rollout, complete desired replica counts and current owned Ready pods. Redis and native
PostgreSQL currently lack readiness probes; their signal is pod readiness, not a database query.

Reads use the persisted target and namespace, full workload identity labels and verified controller
UID ownership (including the Deployment -> ReplicaSet -> Pod chain). PostgreSQL uses its existing
provider-owned native label values. Results recheck the persisted deployment revision after native
reads; replaced evidence returns Superseded. Observations have a 15-second budget, four concurrent
requests, at most 100 pods and 32 warnings across eight verified UIDs. Limited pod results cannot
prove complete readiness. Missing workloads, unavailable targets, forbidden reads and timeouts are
explicit; warning-read failure can coexist with usable pod observations. Responses contain bounded
pod phases/readiness/restarts and allowlisted reasons/counts/times, never raw messages, conditions,
workload specifications, commands, environment, credential material or native exception bodies.

Generic retry requires `expectedRevision` and `expectedOperation` query values and preserves the
existing tenant write check and atomic state/outbox transaction. Stale selections return conflict.
WebServices use their owner retry endpoint to preserve release bookkeeping. Tenant/group summaries
link to resources; supported precise retries appear in the owner's Operations section. Browser
reads are fenced on tenant/session/API scope changes, refresh after acknowledged reconnect and
explicitly mark stale observations. Bearer renewal preserves ordinary drafts, tabs and authorized
runtime streams while the realtime cookie transport renews. The API is authoritative for Viewer versus writer access.

## Runtime logs and Kubernetes diagnostics

`GET /resource-deployments/{deploymentId}/diagnostics` discovers a bounded sample of owned
instances, their eligible regular/init containers and Kubernetes warning events. Providers opt in
through `IResourceDeploymentDiagnosticsProvider` on the same validated descriptor catalog. The
server resolves the tenant-owned placement, native controller and UID ownership chain; callers do
not choose a namespace, target or arbitrary pod name. Failing and earlier-generation owned pods
remain inspectable. Ephemeral containers are excluded. Container discovery retains at most 32
regular and 32 init containers per instance and reports truncation. Warning events retain only
safe reason codes, object names, counts and times, separately from audit and reconciliation state.

`GET /resource-deployments/{deploymentId}/runtime-logs` accepts the discovered instance UID,
container, expected deployment revision, recent/follow mode, previous-container selection and
1–500 tail lines. Viewer access is allowed within the active tenant. Native ownership is verified
before opening, immediately after opening, before returning the first frame, periodically and at
EOF. A pod replacement or superseded placement ends that read explicitly; another pod with the
same name is never selected automatically. Kubernetes' pod-log API has no UID precondition, so
these checks bound replacement races rather than providing an atomic lock on the native read.

Output is finite, UTF-8-safe NDJSON with Opened/Output/Ended frames and fixed terminal reason
codes. `RuntimeDiagnostics` configures `MaximumBytes` (default 1 MiB, allowed 4 KiB–4 MiB),
`LifetimeSeconds` (120, allowed 10–300) and `OwnershipCheckSeconds` (5, allowed 1–10).
Each periodic ownership check has a five-second budget and ends a stalled read with Timeout.
Four readers may run concurrently per API process; a fifth receives CapacityBusy. Closing the
response, switching browser scope or application shutdown cancels and disposes only that read's
upstream response, preserving the cached target client. No runtime output is persisted.

Every reconnect opens a new recent window and may overlap prior output; there is no durable
cursor or archive. Target outages, credentials lacking log permissions, missing/replaced instances,
unsupported providers, lifetime/output bounds and a follow connection ending are explicit.
Generated diagnostics omit raw native messages and configuration, but arbitrary workload output
can contain sensitive values and is displayed as plain text. The browser keeps at most 65,536
characters, reports discarded output and clears it when the view closes or scope changes.

## Shared observation lifecycle

The shared [observation infrastructure](../../shared/Observation/README.md) owns coordination,
source/sampling lifecycle, timing and the Mongo demand/lease/snapshot protocol. This feature retains
its provider catalog, tenant/placement/context fences, sanitized workload BSON mapping and events.
StorageProfiles uses the same execution/storage mechanisms through its own target-scoped adapters.


Realtime interest creates expiring durable demand for a tenant-owned physical deployment.
Registered provider change sources establish bounded native LIST/WATCH ownership before the
server acknowledges coverage. An opaque controller binding, source-control version, deployment
revision/generation, target connection version and owner epoch fence every snapshot. Kubernetes
resource versions remain opaque and local to each native kind/selector; bookmarks are not
readiness evidence. EOF restarts the watch, while 410 or malformed frames require a new LIST.
Missing/foreign controllers, incomplete ownership graphs, stalled headers and source loss withdraw
coverage before slow samples can commit.

Mongo supplies the lease clock. One API replica owns a 30-second CAS lease renewed every ten
seconds. Samples are singleflight with a 250 ms coalesced hint and one trailing sample, using the
shared four-reader/15-second budget. Quiet healthy sources revalidate every 30 seconds.
Unchanged authoritative validation refreshes checked time and retained observation versions.
Snapshot and minimal observation-change outbox records commit atomically; the final transaction
decision rechecks lease/demand deadlines after awaited outbox work. Demand renewal never extends
its original transport expiry. Final interest removal, expiry, source/binding changes, credential
rotation and late old owners cannot revive stale evidence. Inactive snapshot payloads expire after
five minutes; retained control counters prevent version reuse after payload expiry.

`GET /resource-deployments/{id}/observation` and `/diagnostics` prefer a fresh shared snapshot and
otherwise use the bounded live read path. Runtime notifications carry only deployment identity and
versions. Health and diagnostics HTTP responses contain sanitized bounded metadata; log output
continues through its separate uncached, bounded HTTP stream. Native readiness remains separate
from declared connection endpoints, persisted topology, reconciliation and application connectivity.

`ResourceDeployments:Observation:Enabled=false` disables shared observation ownership and
acknowledged runtime coverage, preserving existing HTTP behavior. `MaximumSources` defaults to 64
and `ScanBatchSize` to 128; startup validates their bounds. The composition also disables ownership
when `Realtime:Enabled=false`. Platform storage uses the separate
`StorageProfiles:Observation:Enabled` option and its own admin-scoped target/version lifecycle.

The `Pole.ResourceDeploymentObservations` meter records shared/live/unavailable read selections,
source gain/loss, sample accepted/rejected/cancelled/failed outcomes and sample duration. Labels
are fixed outcomes, without tenant, resource, target, binding, lease or native metadata values.
The `Pole.KubernetesWatch` meter reports bounded LIST/WATCH recovery categories. See
[observability](../../../docs/observability.md) for export and interpretation.
