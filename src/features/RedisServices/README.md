# Redis services

The Redis feature owns the desired state and lifecycle of tenant-scoped Redis services. It
coordinates quota admission, resource placement, durable events, and Kubernetes reconciliation,
while MongoDB remains the source of truth for the resource document.

## Module boundaries

- `RedisServices.Domain` owns the Redis aggregate, specification, lifecycle states, and state
  transitions.
- `RedisServices.Events` owns durable Redis event contracts.
- `RedisServices.Application` owns HTTP application flows, persistence capabilities, deployment
  integration, and Kubernetes mapping.
- `Pole.Server` is the composition root. It provides the Mongo client/database and registers the
  feature persistence module, but `MongoDbContext` does not expose a Redis collection or implement
  a Redis-specific context interface.

Redis is tenant-owned. Every identity lookup and mutation fence must include `TenantId`; callers
must not treat an absent tenant as an unscoped query.

## Persistence capability experiment

Redis is the first feature experimenting with a capability-oriented persistence boundary. Handler
constructors receive named delegates that describe exactly which persistence operations they may
perform. They do not receive `IRedisDbContext`, `IMongoDatabase`, or
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
- `StoreOutboxWrites` and `RunMongoTransaction` are shared narrow capabilities backed by the same
  implementation as the legacy `StoreEventAsync` and `RunInTransactionAsync` extensions. Other
  features can migrate incrementally without maintaining two transaction/outbox semantics.
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

## What this experiment is evaluating

Benefits observed so far:

- Tenant, identity, lifecycle, and revision fences have one implementation path.
- Handler authority is visible from its constructor and is narrower than a feature-wide context.
- Mutation/outbox pairing is harder to forget because it lives inside one operation.
- `MongoDbContext` no longer needs a compile-time dependency on Redis.
- Delegate contracts remain easy to replace with small fakes in tests.

Costs and open questions:

- DI registration is more explicit and has more entries.
- Handlers that need several capabilities gain constructor parameters.
- Write delegates still expose the Mongo session type because they join larger transactions.
- Individual delegates may eventually be grouped into small query/mutation records if constructor
  noise grows, but such records must not grant unrelated persistence authority.
- A separate Redis Mongo project is useful only if a stronger compile-time boundary justifies the
  extra project and the public contracts no longer need Mongo types.

Do not apply this pattern mechanically to every feature. Further experiments on this branch should
compare constructor clarity, stack traces, test ergonomics, transaction visibility, and the effort
required to add a new mandatory persistence invariant.

## Verification

The experiment is covered by Mongo-backed integration tests for tenant isolation, stale revision
conflicts, rollback, finalization atomicity, idempotency, and outbox tenant metadata. API-slice tests
exercise the same registration path as production.

Useful audit searches:

```bash
rg -n "IRedisDbContext|IMongoCollection<Redis>|Builders<Redis>" \
  src/features/RedisServices src/api/Pole.Server
rg -n "StoreEventAsync|RunInTransactionAsync" \
  src/features/RedisServices/RedisServices.Application
```

Related platform rules:

- [Engineering guidelines](../../../docs/engineering-guidelines.md)
- [Multitenancy](../../../docs/multitenancy.md)
- [Observability and outbox propagation](../../../docs/observability.md)
- [Testing](../../../docs/testing.md)
