# Demand-driven observation lifecycle

`Pole.Observation` contains the execution mechanics shared by runtime observation and platform
storage discovery. It has no dependency on feature projects, Kubernetes models, SignalR, Mongo,
provider selectors or serialized integration events. It is not a monitoring/history service.

## Ownership

- `ObservationCoordinator<TKey,TContext,TOwner>` scans durable demand, bounds active owners,
  cancels obsolete work, and releases ownership with a bounded cleanup token.
- `ObservationOwner` consumes source transitions separately from native sampling. Positive hints
  coalesce; only one sample runs at a time, and hints arriving during a sample cause one trailing
  sample. Source loss persists its fence before cancellation, without waiting for a native GET.
- `IObservationCoordination` and `IObservationSession` are feature adapters. A workload adapter
  owns placement/revision/generation and native controller identity; a storage adapter owns target
  connection versions and the complete StorageClass/PV/PVC inventory. Adapters also own metrics.
- `ObservationReadBudget` is one process-wide budget used by native HTTP reads and initial LISTs.
  It checks cancellation after noncooperating reads and includes queue time in the deadline.
- `ObservationWakeup` is a local optimization. Durable demand remains authoritative after restart
  or when the request and the owner execute on different API replicas.

Subscriptions originate from active visible UI interests. Final unsubscribe/expiry stops ownership;
continuous metrics, dashboards, history and alerts belong to the monitoring stack. Storage discovery
is platform-admin scoped and covers the target inventory, including PVCs across namespaces.

## Transactional adapter

`Pole.Database.Observation.MongoObservationStore<TContext>` implements the shared Mongo protocol.
Feature-local stores remain the public typed boundaries and retain collection names, BSON documents,
indexes, validation, sanitized payload mapping and integration event identities.

`IMongoObservationDefinition` supplies a scope, pinned context, transactional context fence,
snapshot acceptance rule, and outbox callback. Tenant workload scopes include BOTH tenant and
physical deployment identity; platform storage scopes use the deployment target. Never drop the
scope when building queries. Context checks and outbox writes use the SAME caller session as the
control/snapshot writes. These callbacks must not perform Kubernetes/network side effects.

The store owns original-session-bounded demand renewal, server-clock lease deadlines, retained
epoch/source/observation counters, transaction retry fencing, final checks after awaited outbox
work, and delayed snapshot cleanup. It intentionally retains counters after snapshot expiry.
A feature must sanitize its payload BEFORE submitting it; no raw Kubernetes specs, arbitrary
metadata, credentials, Secret values or logs belong in these snapshots/outbox notifications.

## Timing and behavior

`ObservationPolicy` owns the existing timing contract: 250 ms coalescing, 30-second leases renewed
every 10 seconds, 30-second confirmation, 45-second freshness, 60-second demand heartbeat bound,
five-minute idle payload retention, and four native reads with a 15-second total budget.
Mongo uses `$$NOW` for persistence deadlines; `TimeProvider` drives local scheduling only.
An older sample cannot withdraw a newly recovered source. Persistence CAS remains the final guard
against old owners, changed contexts and late responses even when a provider ignores cancellation.

No Orleans/Hangfire ownership layer is involved. `ResourceDeployments:Observation:Enabled` and
`StorageProfiles:Observation:Enabled` retain their existing rollback behavior: disabling shared
ownership removes acknowledged coverage and callers use the existing bounded live-read fallback.
HTTP routes, SignalR topics, BSON shapes and polling intervals are unchanged; no migration is needed.

## Verification

The Integration observation family covers both real Mongo adapters, expiry/takeover/rollback,
provider/native reads, production DI, owner source loss, burst/trailing samples and stale responses.
`SharedObservationLifecycleTests` owns engine/read-budget cases that do not require Mongo; feature tests
retain ownership, tenant, storage completeness and protocol-specific checks. Architecture tests
protect feature/project dependency direction. Run a current solution build before test commands.
Before publishing, use the repository's full `scripts/quality_gate.py run`; focused tests do not
replace live Kubernetes, Aspire E2E or other publication lanes.
