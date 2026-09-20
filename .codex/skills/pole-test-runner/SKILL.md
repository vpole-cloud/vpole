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
- Documentation, skills, or agent guidance use structural validation during editing; publication still requires every local test as specified below.

An in-memory or standalone database is not evidence for Mongo transaction, replica-set, race, or concurrency behavior. A mocked HTTP boundary is not evidence for route binding or authentication middleware. A generated manifest is not evidence that a live cluster accepts or reconciles it.

## Execution

- Use the restore, build, infrastructure-free project, and frontend commands maintained in `AGENTS.md`.
- Build the solution once before project-level .NET tests, then use `--no-build --no-restore`.
- Use a focused filter in `Pole.Tests.Integration` when it proves the changed behavior; broaden to the complete suite when shared composition or persistence behavior changed.
- Follow `docs/aspire-e2e-testing.md` for E2E prerequisites and isolation. Use `$pole-aspire-local` only for production startup wiring or a real cross-service workflow.
- Do not claim an infrastructure lane passed when its prerequisites were unavailable.

## Safe Parallelism

- Parallelism does not require subagents. The primary agent can launch independent commands concurrently and summarize their outputs.
- Restore and build once before parallel .NET tests. Then use `--no-build --no-restore` on distinct test projects.
- Do not run concurrent builds: projects share `bin` and `obj` outputs.
- Do not split one test project into simultaneous filtered invocations.
- Run `Pole.Tests.Integration`, Aspire E2E, live Kubernetes tests, and other shared external-resource lanes sequentially.
- Consider Docker, CPU, memory, ports, namespaces, and persisted identities shared even when commands come from different agents.

## Report

Return the commands run, pass/fail counts, the first actionable failure, skipped checks with reasons, and the next check that would add meaningful evidence. Distinguish a test failure from an environment or sandbox failure.

## Delivery Gate

Follow `docs/agent-delivery.md`. Focused checks are development feedback; before opening any PR, run `python3 scripts/quality_gate.py run` on the final source tree. Every local backend, frontend, automation, and infrastructure test must pass with zero skips, including for documentation or tooling PRs. Missing prerequisites and pre-existing failures block publication; draft PRs are not an exception. Use the recorded full-run evidence and review required by AGENTS.md with the publication wrapper described there.

If tests fail, the first sentence of every summary must identify the failing tests and explain the known cause (or say it is still unknown). Report unavailable infrastructure as a blocker, never as a passed test.
