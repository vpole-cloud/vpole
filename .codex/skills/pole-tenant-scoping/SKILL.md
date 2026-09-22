---
name: pole-tenant-scoping
description: Preserve Pole tenant isolation across APIs, persistence, contracts, events, projections, consumers, and frontend tenant selection.
---

# Pole Tenant Scoping

Use this skill to prevent cross-tenant data leaks and inconsistent tenant authorization in Pole.

Read `docs/multitenancy.md`, the owning feature README, and the nearest tenant-scoped endpoint and persistence operation. Determine tenant ownership from the current feature contract instead of maintaining a global resource-name list in this skill.

## Core Rules

- Preserve `TenantId` on tenant-owned domain documents, contract snapshots, search projections, events, and outbox records.
- Declare active-context requirements with local `TenantEndpointMetadata`; bind the selection with `[FromTenantHeader]` in C# or `[<Pole.ASP.Helpers.FromTenantHeader>]` in F#. Middleware resolves/canonicalizes it before binding; Mediator rejects missing context and mismatches, including for platform admins.
- Keep a route target separate through the explicit target-operation policies in `docs/multitenancy.md`. Platform targets and member management do not implicitly consume the active header. Do not add route-prefix or token-path checks to middleware.
- Mark mediator requests with `ITenantReadOperation`, `ITenantWriteOperation`, or `ITenantCreateOperation`. Create operations require a concrete tenant id.
- Combine resource identity filters with tenant filters. Prefer `Builders<T>.Filter.ForTenant(request.TenantId)` plus the resource-specific predicate.
- Resolve cross-feature references through provider-owned tenant-aware Contracts; never read another feature's collection directly.
- Preserve tenant context in events with `EventCore.Create(tenantId)`. Consumers should read `context.Message.EventCore.TenantId` instead of trusting a stale ambient request.
- Platform-admin authorization can bypass membership checks for permitted operations; it never permits an unscoped tenant-data query. Keep tenant filters explicit unless the endpoint intentionally exposes a platform-wide view.

## API And Frontend

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

Use `$pole-test-runner` to select the smallest API, persistence, consumer, or frontend checks that prove the tenant boundary.
