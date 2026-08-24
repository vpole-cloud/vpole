# Redis services

The Redis feature owns the desired state and lifecycle of tenant-scoped Redis services. It
coordinates quota admission, resource placement, durable events, and Kubernetes reconciliation,
while MongoDB remains the source of truth for the resource document.

CPU, memory, extended resources, and Kubernetes scheduling are resolved from a
[`ComputeProfile`](../ComputeProfiles/README.md) and stored in the resource's immutable
`ResourceDeployment` snapshot. Create uses the default profile when no ID is supplied. Update
without an ID preserves that snapshot; an explicit ID adopts the latest eligible revision. Quota
admission uses resolved requests, and StatefulSet manifests are rendered from the persisted
deployment snapshot.

## Module boundaries

- `RedisServices.Domain` owns the Redis aggregate, specification, lifecycle states, and state
  transitions.
- `RedisServices.Events` owns durable Redis event contracts.
- `RedisServices.Application` owns HTTP application flows, persistence capabilities, deployment
  integration, and Kubernetes mapping.
- `Pole.Server` is the composition root. It provides the Mongo client/database and composes the
  feature persistence module. Production code has no general `MongoDbContext`.

Redis is tenant-owned. Every identity lookup and mutation fence must include `TenantId`; callers
must not treat an absent tenant as an unscoped query.

## Persistence capabilities

Handler constructors receive named delegates that describe exactly which persistence operations
they may perform. They do not receive `IRedisDbContext`, `IMongoDatabase`, or
`IMongoCollection<Redis>`.

Each capability file keeps three closely related elements together when applicable:

1. The public named delegate used by consumers.
2. Small public input/output records specific to that capability.
3. One internal Mongo implementation class with one `InvokeAsync` method.

The current capability surface is:

| Capability | Responsibility | Mongo dependencies |
| --- | --- | --- |
| `LoadRedisById` | Tenant-scoped lookup by resource ID | Redis collection |
| `LoadRedisPage` | Tenant-scoped filtering and pagination | Redis collection |
| `PersistCreatedRedis` | Insert resource and enqueue its created event | Redis collection, outbox writer |
| `PersistUpdatedRedis` | Revision-fenced replacement and updated event | Redis collection, outbox writer |
| `BeginRedisDeletion` | Revision-fenced transition to deleting | Redis collection |
| `FinalizeRedisDeletion` | Delete completed resource/deployments and enqueue deleted event | Redis/deployment collections, transaction runner, outbox writer |

`RedisFilters` is the pure foundation shared by these operations:

```text
VisibleToTenant(tenantId)
    -> ById(tenantId, redisId)
        -> AtRevision(currentRedis)
        -> InLifecycle<TLifecycle>(tenantId, redisId)
```

Adding a mandatory Redis visibility or ownership predicate should therefore require changing this
hierarchy once rather than finding every handler query.

## Transaction and outbox rules

- Create and update operations participate in the transaction already owned by quota/deployment
  orchestration. Their delegates accept an active `IClientSessionHandle` and must not start a
  nested transaction.
- A successful Redis mutation and its unconditional Redis event are one persistence operation and
  use the same session.
- Deletion finalization is a self-contained cross-collection operation, so it owns the transaction
  around Redis deletion, deployment cleanup, and the deleted event.
- `StoreOutboxWrites` and `RunMongoTransaction` are the shared narrow transaction and outbox
  capabilities used by the persistence operations.
- Expected revision and duplicate-key conflicts map to `Result` failures. Mongo availability,
  timeout, and unexpected driver exceptions remain exceptions and reach the standard 5xx path.

## DI and raw Mongo access

`AddRedisPersistence` registers the exact typed collections, one internal implementation per
operation, and its exported delegate method group. Production and integration tests use this same
registration method.

Raw `IMongoCollection<Redis>` access is allowed only inside the feature persistence module and
explicit test helpers. Handlers and deployment adapters must consume named capabilities and must
not accept arbitrary Mongo filters or update definitions.

This is intentionally not a generic repository. The delegate signatures use semantic inputs and
return domain values or intention-revealing outcomes; they do not expose `IFindFluent`,
`UpdateResult`, or general query execution.

## Mongo document validation

The `RedisSchemaValidationMigration` makes the Mongo collection an additional enforcement point
for Redis persistence invariants. It creates `RedisServices` with a `$jsonSchema` validator when
the collection is absent and uses `collMod` when it already exists. The validator uses
`validationLevel: strict` and `validationAction: error`.

The migration is a self-contained historical artifact: its collection name, BSON field names,
types, and discriminators are frozen in the migration file. It depends only on the migration
contract and MongoDB driver, not on current Redis domain types or persistence registration. A
future persistence rename therefore requires a new migration instead of rewriting this one.
Small BSON schema helpers are nested in the migration for readability and are not shared with
future migrations, so changing a common helper cannot alter this historical contract.

The schema requires the tenant and resource identity, the complete desired-state specification,
tags, events, revision, and lifecycle. It also validates the exact discriminator and fields of the
active and deleting lifecycle variants. This protects every writer, including scripts and future
persistence operations, instead of relying only on request validation.

Unknown fields are allowed outside the closed lifecycle variants. This keeps additive document
changes deployable without a synchronized schema migration; a newly mandatory field must still be
added to the schema's `required` list before writers rely on it. If Pole starts retaining production
data, tightening the schema should use an explicit audit/backfill rollout before switching strict
validation on.

`MongoDocumentContractTester<TDocument>` keeps the schema independent while checking it against
the current domain serialization. It serializes real domain instances with the MongoDB driver and
asks the validated collection to accept them; negative cases mutate the serialized BSON and assert
MongoDB document-validation error `121`. The Redis tests cover both collection creation and
`collMod`, strict/error settings, tenant ownership, and both lifecycle shapes.

## Verification

The persistence boundary is covered by Mongo-backed integration tests for tenant isolation, stale revision
conflicts, rollback, finalization atomicity, idempotency, and outbox tenant metadata. API-slice tests
exercise the same registration path as production.

Useful audit searches:

```bash
rg -n "IRedisDbContext|IMongoCollection<Redis>|Builders<Redis>" \
  src/features/RedisServices src/api/Pole.Server
rg -n "IRedisDbContext|MongoDbContext|StoreEventAsync" src tests
```

Related platform rules:

- [Architecture history](../../../docs/architecture-history.md)
- [Engineering guidelines](../../../docs/engineering-guidelines.md)
- [Multitenancy](../../../docs/multitenancy.md)
- [Observability and outbox propagation](../../../docs/observability.md)
- [Testing](../../../docs/testing.md)
