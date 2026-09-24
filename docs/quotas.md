# Tenant quotas

Pole enforces tenant quotas before a resource mutation is committed. Quota admission is shared infrastructure; each resource feature owns only the quota definitions specific to that feature and the profile that maps its domain specification to quota values.

## Model

A quota is identified by a stable, string-backed `QuotaKey`. The current shared quotas are:

- `CpuMillis`: requested CPU in millicores across the workload's admitted instance count.
- `MemoryMi`: requested memory in MiB across the workload's admitted instance count.
- `WorkloadInstances`: the admitted instance count.

Features may add their own definitions. RedisServices, WebServices, and Postgres currently add resource-count quotas. There is deliberately no central enum or resource-type switch: a new feature must not require editing a shared list of every possible quota.

All persisted quota quantities use `long`. Units belong to the quota definition, while arithmetic stays exact and integral.

## Definitions and profiles

`Quota<TInput>` calculates one quota for a typed input. Shared CPU, RAM, and instance definitions consume `WorkloadQuotaInput`. A feature-specific quota input combines its logical scaling values with the resolved `ComputeProfile` requests; CPU and memory no longer come from WebService, Redis, or PostgreSQL domain specifications.

For example, `RedisQuotaProfile` combines the resolved per-instance CPU/memory request with one admitted Redis instance and `RedisServiceCountQuota`. PostgreSQL multiplies the resolved per-instance requests by its instance count exactly once. WebService admission uses the maximum admitted replica count. Handlers do not calculate quota vectors or know how admission persistence works.

Quota types and profiles are discovered from an explicit list of feature assemblies passed to `AddQuotaFramework`. Discovery is intentionally bounded. `QuotaCatalog` fails fast for duplicate quota keys, duplicate profiles, unregistered profile keys, and values returned under undeclared keys.

## Placement-aware accounting

Policy and usage are keyed by tenant, not by namespace, RuntimeCell or ResourceGroup.
A resource key identifies the logical resource. Its resource-count quota is charged once.
For placement-backed resources, CPU, memory and workload instances sum the desired
ResourceDeployments. Each placement
uses its persisted ComputeProfile requests multiplied by its fixed replicas, or the shared
maximum replica count when WebService autoscaling is enabled. Changing a ComputeProfile does
not change existing usage until a resource explicitly adopts a new resolved snapshot.

Adding a target reserves only its incremental impact. Updating a target compares the old and
new aggregate vectors; a shared scaling change compares all desired target contributions.
Removing a target releases that target's contribution in the same transaction that requests
its deletion. Its retained `Delete`-desired or `Deleted` deployment record is no longer charged,
even if Kubernetes cleanup has not completed. Pending and failed Apply-desired placements
remain charged: desired Pole state, rather than live readiness or observed usage, is authoritative.
Deleting the logical resource subsequently releases only its remaining desired placements and
its one resource count. Repeated deletion must not release capacity owned by another resource.

Logical-resource and physical-deployment revision fences validate the snapshots used to calculate
a delta inside the mutation transaction. If a concurrent add, remove or update invalidates those
snapshots, the transaction aborts its usage changes and the executor cancels any reservation.
Retries must reload state before calculating a new delta. Kubernetes ResourceQuota and LimitRange
are optional secondary safeguards outside this accounting path.

## Admission lifecycle

Handlers call `IQuotaAdmissionExecutor` with:

1. the tenant id;
2. a feature helper-created `QuotaResourceKey`;
3. an explicit create, update, or delete mutation containing the feature-owned quota input built
   from the resolved compute snapshot and logical scaling values;
4. a callback that persists the resource and its outbox events.

The executor calculates the delta from the registered profile. Positive changes are reserved before the mutation. Inside one Mongo transaction it commits the reservation, applies reductions, persists the resource, and stores outbox events. A returned failure aborts the transaction. If preparation or the transaction fails, the executor makes a best-effort cancellation of the active reservation without masking the original failure.

WebService creation reserves quota before entering the transaction callback;
generated DNS reservations, resource deployments, the logical resource, and
their outbox writes are then persisted in that caller-owned transaction.

Feature-owned persistence capabilities handle reservation and committed-usage updates. Resource handlers use `IQuotaAdmissionExecutor` instead of calling those capabilities directly.

Reservation persistence returns `ReserveQuotaReservationOutcome` with a lease,
`QuotaExceeded`, or `QuotaReservationConflict`. Commit persistence returns
`CommitQuotaReservationOutcome` with committed, missing, cancelled, or expired cases.
Invalid requests remain argument exceptions. `QuotaExceeded` carries the quota key,
limit, requested delta, and current reserved-plus-committed usage. The executor maps
these concrete failures to the provider-owned `QuotaExecutionError` contract.
The optional `Quotas.Contracts.HttpMapping` companion supplies stable default
ProblemDetails representations for consumers without coupling contracts to HTTP.

## Reservation states and cleanup

A reservation has one explicit persisted state:

- `ActiveQuotaReservationState` requires an expiry time;
- `CommittedQuotaReservationState` requires a commit time;
- `CancelledQuotaReservationState` requires a cancellation time.

Every state has a cleanup time. Mongo queries and indexes use `State.Kind`, `State.ExpiresAtUtc`, and `State.CleanupAfterUtc`. TTL cleanup is a safety mechanism for expired/history records; normal failures are compensated immediately.

## API and tenancy

Platform admins manage policies through `/api/v1/platform/tenants/{tenantId}/quota`. A missing key in the policy means unlimited. Unknown keys and non-positive limits are rejected.

Tenants read current committed usage and active reservations through `/api/v1/tenant-quotas/usage` using `X-Pole-Tenant-ID`. Tenant authorization remains owned by the tenant middleware and Mediator behaviors; quota handlers do not repeat header validation.

## Adding quota support to a feature

1. Add feature-specific `Quota<TSpec>` definitions only when shared definitions do not cover the requirement.
2. Add one `QuotaProfile<TSpec>` that composes the feature's shared and local quotas.
3. Add a small resource-key helper so handlers do not build identity strings.
4. Include the feature assembly in the bounded `AddQuotaFramework` registration.
5. Wrap quota-affecting create, update, delete, and rollback operations in `IQuotaAdmissionExecutor`.
6. Keep resource persistence and outbox writes in the executor callback transaction.
7. Test exact profile values, positive and negative deltas, policy denial, compensation, and tenant isolation.

Use `TimeProvider` for quota timestamps. Validate `QuotaSettings` through options validation with `ValidateOnStart`; services must not validate application configuration during arbitrary operations.
