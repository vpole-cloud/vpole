---
name: pole-test-runner
description: Select the smallest evidence-bearing checks for a Pole change and decide which test commands may run safely in parallel.
---

# Pole Test Runner

Choose the cheapest boundary that proves the changed invariant. Do not treat a passing test at the wrong boundary as evidence.

Inspect the changed files, nearest tests, and the verified commands in `AGENTS.md`. Read `docs/testing.md` before changing test strategy or entering an infrastructure-backed lane. Identify the exact invariant each command will prove.

## Test Boundary

- Domain and feature-local logic: run the owning `*.Tests` project when one exists.
- Feature references, forbidden APIs, endpoint boundaries, or module composition: run `Pole.Tests.Architecture`.
- Handlers, Mediator pipelines, Mongo persistence, transactions, pagination, filtering, API binding, auth, or result mapping: run focused `Pole.Tests.Integration` tests.
- Kubernetes object shape: run manifest tests; these are not evidence for live apply/replace/delete behavior.
- Kubernetes client behavior: use a small live-cluster test only when manifests or fakes cannot prove it.
- Production DI, Keycloak, RabbitMQ, browser flows, or cross-service behavior: use Aspire E2E.
- Frontend logic: run Vitest. TypeScript and production bundling: run the build. React and lint rules: run lint.
- Documentation, skills and proven formatting use the structural publication lane; executable assets, fixtures and unproven changes retain behavioral verification. Templates are checked through generated code. Follow the complete-diff criteria in `docs/agent-delivery.md`.

An in-memory or standalone database is not evidence for Mongo transaction, replica-set, race, or concurrency behavior. A mocked HTTP boundary is not evidence for route binding or authentication middleware. A generated manifest is not evidence that a live cluster accepts or reconciles it.

## Execution

- Use the restore, build, infrastructure-free project, and frontend commands maintained in `AGENTS.md`.
- Build the solution once before project-level .NET tests, then use `--no-build --no-restore`.
- Use a focused filter in `Pole.Tests.Integration` when it proves the changed behavior; broaden to the complete suite when shared composition or persistence behavior changed.
- Follow `docs/aspire-e2e-testing.md` for E2E prerequisites and isolation. Use `$pole-aspire-local` only for production startup wiring or a real cross-service workflow.
- Do not claim an infrastructure lane passed when its prerequisites were unavailable.

## Safe Parallelism

Follow [test scheduling](../../../docs/testing.md#parallelism-across-worktrees).

- Parallelism does not require subagents; the primary agent can coordinate commands directly.
- Within one worktree, restore/build once, then run distinct infrastructure-free projects with
  `--no-build --no-restore`. Do not overlap builds, a build with tests using its outputs, or multiple
  invocations of the same test project. Serialize infrastructure lanes within that worktree.
- Separate worktrees have isolated test resources, including Docker containers and Kubernetes
  namespaces. Docker-backed suites may run concurrently across worktrees; they are relatively light.
- Kubernetes-backed suites may also run concurrently across worktrees. Queue these heavy runs by
  default for host capacity, not because their test resources collide. The primary agent coordinates
  overlap against the host's current CPU, memory, disk and other active work.
- Keep worktree/run ownership and scoped cleanup intact. Shared lab lifecycle operations retain their
  existing lock; do not stop/reset shared services during other tests.
- The quality-gate runner bounds concurrent projects separately for Fast, Services, Kubernetes and E2E.
  Local limits default to 4, 2, 1 and 1. CI defaults to 2, 1, 1 and 1. Groups execute sequentially.

## Report

Return the commands run, pass/fail counts, the first actionable failure, skipped checks with reasons, and the next check that would add meaningful evidence. Distinguish a test failure from an environment or sandbox failure.

## Delivery Gate

Follow `docs/agent-delivery.md` for selection from the complete PR diff. Run `python3 scripts/quality_gate.py plan` and `run --profile pr`: docs/proven formatting use structural checks; templates use generated-code checks; behavioral or unproven changes retain complete PR suites. Use `--profile full` for broad/infrastructure work or explicitly fresh all-lane evidence. Selected tests require zero failures/skips; unavailable unselected infrastructure does not block reduced verification. Reuse behavioral results only through the gate's committed same-base safe-delta proof, recording both SHAs; never relabel stale results manually. Use `record-review`, `verify` and the authorized publication wrapper.

If tests fail, the first sentence of every summary must identify the failing tests and explain the known cause (or say it is still unknown). Report unavailable selected infrastructure as a blocker, never as a passed test.
