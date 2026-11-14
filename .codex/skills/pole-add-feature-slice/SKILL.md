---
name: pole-add-feature-slice
description: Implement a Pole capability that spans multiple feature layers, such as domain, persistence, API, contracts or events, reconciliation, search, or frontend.
---

# Pole Feature Slice

Use this implementation guide for an agreed cross-layer change. Use `$feature` when the
task also needs a staged plan and approval.

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

Use the [first-slice checkpoint](../../../docs/agent-delivery.md#review-the-first-slice-before-repeating-it)
to record selected references, the inspected slice and its comparison before repeating the pattern.
Complete the [structure and pattern pass](../../../docs/review-checklist.md#structure-and-pattern-pass)
over the resulting diff, including supporting features.

When changing the starter or generator, run `python3 scripts/check_feature_template.py`: it tests
the materialized projects with production analyzers, the existing architecture suite and real
Mongo/API tests. For proven template formatting use `--format-only`; README-only changes use structural checks.
Generator unit tests alone are not evidence that semantic template changes work. Use the
[review checklist](../../../docs/review-checklist.md) before handoff.

1. Model valid states and transitions in Domain, using factories or methods that preserve invariants.
2. Add feature-owned persistence operations, migrations, indexes, and transaction boundaries. Join a caller-owned session only when a published contract requires atomic cross-feature work.
3. Keep one Minimal API endpoint, its transport types, validator, and Mediator handler in one endpoint file.
4. Persist integration events through `Outbox.Contracts` in the same Mongo transaction as the mutation. Run external side effects from consumers after commit.
5. Register feature services, mappings, migrations, and consumers through the feature registration entry point; keep `Pole.Server` as composition only.
6. Add search, Kubernetes, or frontend support only when the user workflow requires it.

Load the focused Pole skill when the slice touches tenant scoping, outbox delivery, Kubernetes resources, or the frontend. Use `$pole-test-runner` to select tests that prove each changed boundary, and add manifest tests before live-cluster checks.

For publication, use the [complete-diff gate](../../../docs/agent-delivery.md#local-and-ci-gate-profiles).
