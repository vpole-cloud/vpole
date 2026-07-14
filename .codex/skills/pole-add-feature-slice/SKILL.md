---
name: pole-add-feature-slice
description: End-to-end workflow for adding or extending a Pole feature slice. Use when creating a new resource type or significant capability across domain models, application endpoints, events, contracts, Mongo context, dependency injection, API mapping, search projection, Kubernetes reconciliation, tests, and optional frontend integration in this modular monolith.
---

# Pole Feature Slice

Use this skill when a change crosses more than one layer of a Pole feature.

## First Steps

1. Inspect the relevant feature and nearby implementation before editing.
2. Read `docs/engineering-guidelines.md`, `docs/architecture_overview.md`, and the relevant focused docs: `docs/multitenancy.md`, `docs/observability.md`, `docs/search.md`, or `docs/testing.md`.
3. Inspect the closest existing feature before adding new structure.

## Current Shape

- Feature projects live under `src/features/<Feature>/<Feature>.(Domain|Application|Events|Contracts)`.
- C# feature examples: `WebServices`, `RedisServices`, `ResourceGroups`, `DnsRecords`, `Users`, `Tenants`.
- F# feature examples: `Postgres`, `Search`.
- Shared primitives live under `src/shared`, including mediator, multitenancy, database, observability, events, result types, ASP helpers, Kubernetes helpers, and models.
- API composition happens in `src/api/Pole.Server/Extensions/DependencyInjection.cs` and `src/api/Pole.Server/Extensions/MapApi.cs`.

## Backend Workflow

1. Define or update the domain model with factory methods. Prefer existing factory patterns over direct construction.
2. Add DTOs and mapping extensions in the feature application `Common` folder.
3. Add one Minimal API endpoint per file, keeping its route mapping, request/response types, validator, and Mediator handler together. Group endpoint files in `Endpoints` subfolders when useful. Keep command handlers transactional where they mutate state.
4. Add events in the feature `Events` project and extension/factory methods that create `EventCore` correctly.
5. Store events in the outbox inside the same Mongo transaction as the mutation.
6. Add consumers for asynchronous side effects such as Kubernetes reconciliation, search indexing, or follow-up domain workflows.
7. Update context interfaces and `MongoDbContext` only for collections the feature owns.
8. Register services, validators, migrations, consumers, settings, and endpoint mapping in the server composition layer.

## Cross-Feature Rules

- Use contracts for narrow cross-feature access. Current examples include `IResourceGroupLookup` and `IDomainNameReservationService`.
- Return `Result` for expected, meaningful business outcomes. Let MongoDB availability and driver exceptions propagate to the global exception handler; do not wrap every persistence call in a generic failure result.
- Use `Result<bool>` only when the boolean is a meaningful successful answer. For a command guard, prefer an intention-revealing `Task<Result>` contract whose success means the guard passed and whose `Conflict` means it did not.
- Do not expose another feature's aggregate collection through a consuming feature context just to validate placement or ownership.
- For tenant-owned resources, load referenced resource groups through `IResourceGroupLookup.GetPlacementAsync`.
- For DNS names, preserve the DNS-owned reservation flow and global domain uniqueness.
- Keep tenant authorization, outbox reliability, and search projection requirements visible in the first implementation pass.

## Search And Frontend

- If the resource should appear in global search, update `docs/search.md` assumptions if behavior changes, add source/projection documents, and add create/update/delete consumers.
- If the resource appears in the SPA, update `src/api/frontend/src/types.ts`, `api.ts`, hooks, pages/components, and active tenant header flow together.

## Tests

- Add integration tests for persistence, transaction behavior, pagination/filtering, tenant scope, and outbox messages.
- Add manifest tests before live Kubernetes tests for infrastructure-backed resources.
- Add API slice tests for route binding, auth/header behavior, or HTTP result mapping.
- Run targeted tests first through `$pole-test-runner`, then the integration suite when the blast radius is broad.
