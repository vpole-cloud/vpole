---
name: pole-add-feature-slice
description: Implement a Pole capability that spans multiple feature layers, such as domain, persistence, API, contracts or events, reconciliation, search, or frontend.
---

# Pole Feature Slice

Read `docs/engineering-guidelines.md`, `docs/module-boundaries.md`, and the owning feature README. Inspect the closest feature with the same architectural needs; feature folders are capability-shaped and do not all contain the same projects.

## Design The Boundary

- Decide which feature owns the behavior and data before choosing files.
- Add only the project roles the capability needs: Domain and Application, plus optional Primitives, Contracts, Contracts.HttpMapping, Events, Runtime, or Tests as defined in `docs/module-boundaries.md`.
- Use provider-owned Contracts for synchronous cross-feature capabilities and provider-owned Events for asynchronous integration. Do not reference another feature's Domain or Application.
- Keep persistence feature-owned under Application. Expose narrow operations or provider contracts and register their Mongo implementations; do not introduce a central feature context or general `MongoDbContext`.
- Let expected business outcomes remain typed. Let unexpected infrastructure and cancellation exceptions reach the standard exception path.

## Implement The Slice

1. Model valid states and transitions in Domain, using factories or methods that preserve invariants.
2. Add feature-owned persistence operations, migrations, indexes, and transaction boundaries. Join a caller-owned session only when a published contract requires atomic cross-feature work.
3. Keep one Minimal API endpoint, its transport types, validator, and Mediator handler in one endpoint file.
4. Persist integration events through `Outbox.Contracts` in the same Mongo transaction as the mutation. Run external side effects from consumers after commit.
5. Register feature services, mappings, migrations, and consumers through the feature registration entry point; keep `Pole.Server` as composition only.
6. Add search, Kubernetes, or frontend support only when the user workflow requires it.

Load the focused Pole skill when the slice touches tenant scoping, outbox delivery, Kubernetes resources, or the frontend. Use `$pole-test-runner` to select tests that prove each changed boundary, and add manifest tests before live-cluster checks.
