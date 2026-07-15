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

`Quota<TInput>` calculates one quota for a typed input. Shared CPU, RAM, and instance definitions consume `WorkloadQuotaInput`. A feature-specific `QuotaProfile<TSpec>` composes shared and local definitions and returns the complete quota impact for one resolved domain specification.

For example, `RedisQuotaProfile` combines the shared workload quotas with `RedisServiceCountQuota`. Resource handlers pass a `RedisSpec`; they do not calculate vectors or know how admission persistence works.

Quota types and profiles are discovered from an explicit list of feature assemblies passed to `AddQuotaFramework`. Discovery is intentionally bounded. `QuotaCatalog` fails fast for duplicate quota keys, duplicate profiles, unregistered profile keys, and values returned under undeclared keys.

## Admission lifecycle

Handlers call `IQuotaAdmissionExecutor` with:

1. the tenant id;
2. a feature helper-created `QuotaResourceKey`;
3. an explicit create, update, or delete mutation containing the resolved domain specification;
4. a callback that persists the resource and its outbox events.

The executor calculates the delta from the registered profile. Positive changes are reserved before the mutation. Inside one Mongo transaction it commits the reservation, applies reductions, persists the resource, and stores outbox events. A returned failure aborts the transaction. If preparation or the transaction fails, the executor makes a best-effort cancellation of the active reservation without masking the original failure.

WebService creation uses the executor's preparation callback so ordering remains quota reservation, DNS reservation, then Mongo transaction.

`MongoQuotaAdmissionService` owns only low-level reservation and committed-usage persistence. Feature handlers must not call it directly.

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
