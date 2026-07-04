# Feature Boundary Decoupling Plan

Status: proposal

## Goal

Reduce feature-to-feature coupling around DNS records, resource group placement, and tenant scope without hiding important application invariants behind generic wrapper services.

The target shape is a modular monolith boundary:

* Resource features own their aggregates.
* Cross-feature access goes through narrow application services.
* Domain models keep stable ids and value snapshots.
* Command handlers keep transactional consistency for invariants that must be true when the command returns.
* Events remain the boundary for asynchronous side effects such as Kubernetes reconciliation, search indexing, and follow-up projections.

## Current Coupling

### DNS records

`WebServices` currently depends on DNS internals in several ways:

* `IWebServiceDbContext` exposes `IMongoCollection<DnsRecord>`.
* `CreateWebServiceHandler` queries and creates `DnsRecord` directly.
* `DeleteWebServiceHandler` deletes the DNS record directly.
* `WebService` stores `DnsRecordShortInfo`, which is acceptable as a value snapshot.

There is also an event-driven DNS deletion path:

* `DeleteEvent` implements `IDeleteDnsRecordEvent`.
* `DeleteDnsRecordFilter<T>` publishes `DeleteDnsRecordEvent`.
* `DeleteDnsRecordEventConsumer` deletes the DNS record.

That means DNS deletion is partly synchronous and partly event-driven. We should make the ownership rule explicit before adding more resource types.

### Resource groups

`WebServices`, `RedisServices`, and `Postgres` all need to validate that a referenced resource group exists in the active tenant and to read its Kubernetes namespace.

Today those features expose `ResourceGroups` on their DbContext interfaces and query the collection directly. This works, but it spreads the same placement rule across features.

### Tenants

`TenantId` on resource documents is expected. The shared multitenancy contracts and mediator authorization behavior are the right coupling point for tenant scope.

We should not introduce a broad tenant service unless a handler needs tenant aggregate data beyond authorization scope.

## Boundary Shapes

### Shape 1: DNS reservation service

Introduce a DNS-owned application service for domain reservations.

Suggested contract:

```csharp
public interface IDomainNameReservationService
{
    Task<Result<DnsRecordShortInfo>> ReserveAsync(
        ReserveDomainNameRequest request,
        CancellationToken cancellationToken);

    Task<Result> CommitAsync(
        CommitDomainNameRequest request,
        CancellationToken cancellationToken);

    Task<Result> ReleaseAsync(
        ReleaseDomainNameRequest request,
        CancellationToken cancellationToken);
}

public sealed record ReserveDomainNameRequest(
    string TenantId,
    string DomainName,
    ResourceType ResourceType,
    string ResourceId,
    string ResourceName,
    string ResourceGroupId);

public sealed record ReleaseDomainNameRequest(
    string TenantId,
    string DnsRecordId);

public sealed record CommitDomainNameRequest(
    string TenantId,
    string DnsRecordId,
    string ResourceId);
```

Recommended location:

* Contract and implementation: `src/features/DnsRecords/Core/Common` or `src/features/DnsRecords/Core/Services`.
* Keep `DnsRecord`, `DnsRecordFactory`, and DNS collection access private to the DNS feature.

Tradeoff:

* A no-session service contract is cleaner and better decoupled from MongoDB.
* Without a shared transaction, DNS reservation and resource persistence become a small saga.
* The saga must have idempotent operations, explicit reservation state, and cleanup for partial failures.
* Strict atomicity across `WebService`, `DnsRecord`, and outbox writes still requires either a shared transaction context or moving orchestration into a component that owns both writes.

Decision:

* Do not expose `IClientSessionHandle` from DNS service contracts.
* Use a synchronous DNS reservation service for create commands so duplicate domains still fail before the API returns.
* Reserve DNS names as pending records, then commit them asynchronously after the resource transaction commits.
* Use cleanup for the rare case where DNS reservation succeeds but web service persistence fails before a commit event is written.
* Keep domain uniqueness global by `Domain`, not tenant-scoped, because host names are cluster-wide.
* Use Mongo duplicate-key handling as the final race-condition guard, even if the service also checks first for a clearer conflict message.

No-session options:

1. Pending reservation with outbox commit.
   Reserve the DNS name first as `Pending`, persist the resource and a DNS commit event second, then let a DNS consumer mark the record as `Committed`. This preserves immediate conflict responses, keeps the contract clean, and uses the existing outbox as the after-commit trigger. It needs idempotent commit/release and cleanup for stale pending reservations.
2. Fully asynchronous DNS workflow.
   Persist the resource as pending, publish a domain-reservation request, and let DNS records publish reserved or rejected events. This is the cleanest boundary, but changes API behavior because domain conflicts are discovered after the create command returns.
3. Application unit of work abstraction.
   Hide Mongo sessions behind a shared `IUnitOfWork` or transaction accessor. This keeps strict atomicity and removes `IClientSessionHandle` from feature contracts, but it still couples services to an ambient transaction model and adds infrastructure complexity.

Recommendation:

* Use option 1 for the first refactor.
* Revisit option 2 if we later want resource creation to become fully workflow-driven.
* Avoid option 3 unless strict atomicity becomes more important than keeping the boundary simple.

### Shape 2: Web service DNS ownership

`WebService` should not own the DNS aggregate. It may keep a stable snapshot:

```csharp
public record DnsRecordShortInfo(string Id, string Domain);
```

That snapshot is useful because Kubernetes ingress generation needs the domain without re-querying DNS.

Create flow:

1. Create the `WebService` id first.
2. Reserve the DNS name through `IDomainNameReservationService`, passing the generated web service id.
3. Build the `WebService` with the returned `DnsRecordShortInfo`.
4. Start one Mongo transaction for the web service and outbox writes.
5. Insert the `WebService`.
6. Store the `CreateEvent`.
7. Store a DNS commit event for the reserved DNS record.
8. Commit.
9. A DNS consumer handles the commit event and calls `CommitAsync`.
10. If the web service transaction fails before the commit event is written, the pending DNS reservation remains locked until cleanup releases it.

The DNS commit event should be written in the same transaction as the web service and outbox create event. That makes `CommitAsync` run only after `addWebService()` is durably committed.

Prefer a DNS-owned commit event, for example `CommitDomainNameEvent`, over a DNS consumer that directly consumes `WebServices.Events.CreateEvent`. `WebServices` can write the DNS commit event because it already calls the DNS reservation contract, while DNS stays independent of web-service event shapes.

If the commit event is not processed immediately, there is no rollback of the already-created web service. The web service stores the reserved domain snapshot and remains created; the DNS record stays `Pending` until the outbox processor retries the commit event. During that window the domain remains locked for duplicate reservations but hidden from DNS list/read endpoints. Operationally, a stuck pending commit should be treated as an outbox/consumer failure to retry or repair, not as a resource creation rollback.

Delete flow:

1. Start one Mongo transaction.
2. Delete the `WebService`.
3. Store the `DeleteEvent`.
4. Commit.
5. Keep the existing DNS delete event path and make the release consumer idempotent.

Important adjustment:

* Remove direct DNS collection access from `IWebServiceDbContext`.
* Remove `Pole.Feature.DnsRecords.Domain` references from `WebServices.Core`.
* Remove direct handler deletion of DNS records. Web service deletion should publish the delete event, and DNS records should own the release.

### Shape 2a: DNS reservation lifecycle

Add lifecycle state to DNS records:

```csharp
[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(Pending), typeof(Committed))]
public abstract record DnsRecordStatus;

public sealed record Pending(
    DateTime ReservedAtUtc,
    DateTime ExpiresAtUtc) : DnsRecordStatus;

public sealed record Committed(
    DateTime CommittedAtUtc) : DnsRecordStatus;
```

Recommended document shape:

* `Status` is a discriminated BSON value.
* `Pending` carries reservation and cleanup timestamps.
* `Committed` carries the commit timestamp.

Rules:

* `ReserveAsync` creates a `Pending` record with resource identity already attached.
* `CommitAsync` changes `Pending` to `Committed` when tenant id, DNS record id, and resource id match.
* `CommitAsync` is idempotent if the record is already committed for the same resource.
* `ReleaseAsync` is idempotent for missing records.
* Domain uniqueness applies to both `Pending` and `Committed` records.
* Domain list/read endpoints should either filter to `Committed` records or expose `Status` explicitly. The safer first choice is to filter to `Committed` records so users do not see abandoned reservations.

### Shape 3: Resource group placement resolver

Introduce a resource-group-owned resolver for placement data.

Suggested contract:

```csharp
public interface IResourceGroupPlacementResolver
{
    Task<Result<ResourceGroupPlacement>> ResolveAsync(
        string tenantId,
        string resourceGroupId,
        CancellationToken cancellationToken);
}

public sealed record ResourceGroupPlacement(
    string ResourceGroupId,
    string TenantId,
    string KubernetesNamespace);
```

Recommended location:

* Contract and implementation: `src/features/ResourceGroups/Core/Services`.

Usage:

* `WebServices` create handler asks for placement, then uses `placement.KubernetesNamespace`.
* `RedisServices` create handler asks for placement.
* `Postgres` create handler asks for placement.

Decision:

* This resolver should return only placement data needed by other features.
* It should not return `ResourceGroup` unless the consuming feature truly needs the full aggregate.
* It should validate tenant ownership internally.

### Shape 4: Tenant scope stays shared infrastructure

Do not extract a general tenant service for this refactor.

Keep:

* `TenantId` on tenant-owned documents.
* `TenantHeaders.TenantId`.
* `ITenantReadOperation`, `ITenantWriteOperation`, and `ITenantCreateOperation`.
* `TenantAuthorizationBehavior`.
* `Builders<T>.Filter.ForTenant(...)` or equivalent tenant filters in persistence logic.

The tenant feature owns tenant and member management. Resource features only depend on tenant scope and authorization contracts.

### Shape 5: Project dependency shape

There are two reasonable project-reference options.

Option A: consume feature `Core` projects directly.

* `WebServices.Core` references `DnsRecords.Core` for `IDomainNameReservationService`.
* `WebServices.Core`, `RedisServices.Core`, and `Postgres.Core` reference `ResourceGroups.Core` for `IResourceGroupPlacementResolver`.
* This is the smallest change.
* The downside is that a feature consumes another feature's full Core assembly, including endpoints and consumers.

Option B: introduce small contracts projects.

* Add contracts-only projects such as `DnsRecords.Contracts` and `ResourceGroups.Contracts`.
* Implement contracts in the owning Core project.
* Consuming features depend only on contracts.
* This is cleaner long-term, but adds more project structure now.

Recommendation:

* Start with Option A if we want a low-risk first refactor.
* Move to Option B only if Core-to-Core references become noisy or cyclic.

## Implementation Plan

### Phase 1: DNS reservation

1. Add `IDomainNameReservationService` and request records.
2. Add `DnsRecordStatus` as a `Pending`/`Committed` domain union.
3. Implement reservation, commit, and release in the DNS feature using `IDnsRecordDbContext`.
4. Add a DNS commit event and consumer that calls `CommitAsync`.
5. Register the DNS reservation service and commit consumer.
6. Add or confirm a unique Mongo index for `DnsRecord.Domain`.
7. Update domain name read endpoints to filter to committed records unless we decide to expose status.
8. Update `CreateWebServiceHandler` to reserve through the service before the web service transaction.
9. Update `CreateWebServiceHandler` to store the DNS commit event inside the web service transaction.
10. Keep the existing `IDeleteDnsRecordEvent` path for deletes.
11. Make the DNS delete consumer call the reservation service or share the same release implementation.
12. Remove `DnsRecords` from `IWebServiceDbContext`.
13. Remove web-service use of `DnsRecordFactory` and `DnsRecord` domain types.
14. Remove direct DNS deletion from `DeleteWebServiceHandler`.

### Phase 2: Resource group placement

1. Add `IResourceGroupPlacementResolver`.
2. Implement it in the resource groups feature using `IResourceGroupDbContext`.
3. Register it in dependency injection.
4. Update web service creation to use the resolver.
5. Update Redis creation to use the resolver.
6. Update Postgres creation to use the resolver.
7. Remove `ResourceGroups` from `IWebServiceDbContext`, `IRedisDbContext`, and `IPostgresDbContext` if no remaining handlers need it.

### Phase 3: Cleanup and consistency

1. Remove unused project references to `DnsRecords.Domain` from service feature Core projects.
2. Remove unused project references to `ResourceGroups.Domain` from service feature Core projects where possible.
3. Keep shared value models such as `DnsRecordShortInfo` only where they are true snapshots.
4. Review event payloads to make sure they carry ids and snapshots, not foreign aggregates.
5. Review feature boundaries for remaining cross-feature coupling.

### Phase 4: Tests

Add or update integration tests for:

* Web service create persists a DNS record through the reservation service.
* Web service create initially creates the DNS record as pending.
* Web service create writes a DNS commit event in the same transaction as the web service create event.
* DNS commit consumer marks the DNS record as committed.
* Web service create returns conflict when a domain already exists globally.
* Stale pending DNS reservations can be released without deleting committed records.
* Web service delete removes the web service and schedules or performs DNS release through the chosen release path.
* Web service create rejects a resource group from another tenant.
* Redis create rejects a resource group from another tenant.
* Postgres create rejects a resource group from another tenant.
* Domain search/list endpoints still return DNS records correctly.
* Outbox messages are still written inside the same transaction as resource mutations.

Run:

```bash
dotnet test tests/Pole.Tests.Integration/Pole.Tests.Integration.csproj -maxcpucount:1 -nodeReuse:false
```

After code changes, run:

```bash
```

## Open Decisions Before Coding

1. Should we start with Core-to-Core references, or create contracts projects now?
2. Do we accept event-driven DNS release on web service delete, rather than immediate DNS deletion inside the delete handler?
3. Should DNS reservation normalize domains before uniqueness checks, for example lower-casing and trimming trailing dots?
4. Should `DnsRecordShortInfo` remain in `shared/Models`, or move to a DNS contracts area if contracts projects are introduced?
5. Should resource placement return only `KubernetesNamespace`, or include resource group name for future event/search use?
6. Should pending DNS reservations be hidden from domain list/read endpoints, or exposed with status?
7. What grace period should cleanup use before releasing pending reservations that were never committed?

## Preferred First Slice

Start with DNS reservation for web services only.

This slice is small, validates the service shape, removes the strongest feature leak, and gives us a concrete pattern before touching Redis and Postgres placement.
