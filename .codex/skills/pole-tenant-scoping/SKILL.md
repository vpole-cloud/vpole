---
name: pole-tenant-scoping
description: Guardrails for adding, changing, or reviewing tenant-scoped behavior in Pole. Use when editing tenant-owned resource APIs, request models, mediator handlers, Mongo filters, consumers, search projections, frontend tenant header behavior, or tests involving `X-Pole-Tenant-ID`, `TenantId`, tenant roles, or platform-admin access.
---

# Pole Tenant Scoping

Use this skill to prevent cross-tenant data leaks and inconsistent tenant authorization in Pole.

## First Steps

1. Inspect the relevant feature and nearby implementation before editing.
2. Read `docs/multitenancy.md` for the canonical model and request flow.
3. Compare against nearby implemented features before inventing a new pattern.

## Core Rules

- Treat these documents as tenant-owned unless the task explicitly says otherwise: `ResourceGroup`, `WebService`, `Redis`, `PostgresCluster`, `DnsRecord`, `SearchDocument`, `SearchSourceDocument`, and `OutboxMessage`.
- Add or preserve `TenantId` on tenant-owned domain documents, snapshots, search projections, and outbox records.
- Accept the active tenant through `[FromHeader(Name = TenantHeaders.TenantId)]` in C# or `[<FromHeader(Name = TenantHeaders.TenantId)>]` in F#.
- Mark mediator requests with `ITenantReadOperation`, `ITenantWriteOperation`, or `ITenantCreateOperation`. Create operations require a concrete tenant id.
- Combine resource identity filters with tenant filters. Prefer `Builders<T>.Filter.ForTenant(request.TenantId)` plus the resource-specific predicate.
- Validate cross-resource references inside the same tenant. For resources placed into resource groups, use `IResourceGroupLookup.GetPlacementAsync(tenantId, resourceGroupId, cancellationToken)`.
- Preserve tenant context in events with `EventCore.Create(tenantId)`. Consumers should read `context.Message.EventCore.TenantId` instead of trusting a stale ambient request.
- Remember the platform-admin nuance: platform admins can run some reads or updates without tenant membership, so handlers still need explicit tenant-aware filters and careful broad-scope behavior.

## API And Frontend

- Resource routes under `/api/v1/resource-groups`, `/api/v1/web-services`, `/api/v1/redis-services`, `/api/v1/postgres`, `/api/v1/domain-names`, and `/api/v1/search` are tenant-scoped.
- The SPA sends `X-Pole-Tenant-ID` from the active tenant stored under `pole.activeTenantId`.
- Tenant listing and auth-provider calls should not require a tenant header unless the backend endpoint requires it.
- When adding a frontend API call for a tenant-owned resource, thread `tenantId` through `api.ts`, the hook, and the page component.

## Tests

Cover the cheapest boundary that proves the tenant rule:

- Same-id or same-name resources in different tenants do not collide unless the invariant is intentionally global.
- Cross-tenant resource group references are rejected.
- Missing or invalid tenant headers fail for create operations.
- Reads, updates, deletes, and consumers do not touch another tenant's documents.
- Search and outbox projections carry the correct `TenantId`.

Use `tests/Pole.Tests.Integration` patterns and run targeted filters before the full suite when possible.