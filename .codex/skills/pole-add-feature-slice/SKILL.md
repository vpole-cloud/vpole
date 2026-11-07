---
name: pole-add-feature-slice
description: Implement a Pole capability that spans multiple feature layers, such as domain, persistence, API, contracts or events, reconciliation, search, or frontend.
---

# Pole Feature Slice

Read `docs/engineering-guidelines.md`, `docs/module-boundaries.md`, and the owning feature README. Inspect the closest feature with the same architectural needs; feature folders are capability-shaped and do not all contain the same projects.

## Design The Boundary

- Decide which feature owns the behavior and data before choosing files.
- Add only the project roles the capability needs: Domain and Application, plus optional Primitives, Contracts, Contracts.HttpMapping, Events, Migrations, Runtime, or Tests as defined in `docs/module-boundaries.md`.
- Use provider-owned Contracts for synchronous cross-feature capabilities and provider-owned Events for asynchronous integration. Do not reference another feature's Domain or Application.
- Keep persistence feature-owned under Application. Expose narrow operations or provider contracts and register their Mongo implementations; do not introduce a central feature context or general `MongoDbContext`.
- Let expected business outcomes remain typed. Let unexpected infrastructure and cancellation exceptions reach the standard exception path.

## Implement The Slice

For a new C# tenant-owned metadata feature, read the [working starter](../../../templates/feature/README.md).
After scope/branch authorization, use `scripts/create_feature.py` with the agreed feature, resource
and route names. It scaffolds only: complete the explicit solution, architecture-role, migration,
event-catalog and endpoint composition steps in the guide. Inspect one complete Create path and
its real Mongo/outbox regression before repeating the pattern for other operations.

Use only the roles and behavior the capability needs. For an existing feature, extend its current
slice. For Kubernetes resources or more complex workflows, choose the maintained production
references rather than treating the metadata example as complete admission/lifecycle behavior.
Rules stay in the canonical docs; the starter is an executable example, not another rulebook.

When changing the starter or generator, run `python3 scripts/check_feature_template.py`: it tests
the materialized projects with production analyzers, the existing architecture suite and real
Mongo/API tests. Generator unit tests alone are not evidence that the example works. Use the
[review checklist](../../../docs/review-checklist.md) before handoff.

1. Model valid states and transitions in Domain, using factories or methods that preserve invariants.
2. Add feature-owned persistence operations, migrations, indexes, and transaction boundaries. Join a caller-owned session only when a published contract requires atomic cross-feature work.
3. Keep one Minimal API endpoint, its transport types, validator, and Mediator handler in one endpoint file.
4. Persist integration events through `Outbox.Contracts` in the same Mongo transaction as the mutation. Run external side effects from consumers after commit.
5. Register feature services, mappings, migrations, and consumers through the feature registration entry point; keep `Pole.Server` as composition only.
6. Add search, Kubernetes, or frontend support only when the user workflow requires it.

Load the focused Pole skill when the slice touches tenant scoping, outbox delivery, Kubernetes resources, or the frontend. Use `$pole-test-runner` to select tests that prove each changed boundary, and add manifest tests before live-cluster checks.

## Delivery Gate

Follow `docs/agent-delivery.md`. Focused checks are development feedback; before opening any PR, run `python3 scripts/quality_gate.py run` on the final source tree. Every local backend, frontend, automation, and infrastructure test must pass with zero skips, including for documentation or tooling PRs. Missing prerequisites and pre-existing failures block publication; draft PRs are not an exception. Use the recorded full-run evidence and review required by AGENTS.md with the publication wrapper described there.

If tests fail, the first sentence of every summary must identify the failing tests and explain the known cause (or say it is still unknown). Report unavailable infrastructure as a blocker, never as a passed test.

Before implementation, name the exact canonical compiled/tested files for the affected patterns. The primary agent reviews one representative slice against those references before the pattern is repeated; this is an internal checkpoint, not another user approval. Add a focused automated safeguard for recurring mistakes where the invariant is mechanically checkable. Existing explicit user approval satisfies the corresponding approval gate.
