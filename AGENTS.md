# Pole Agent Guide

Pole is a self-hosted control plane for deploying and operating workloads on Kubernetes. The backend is a .NET modular monolith, the control-plane UI is a Vite/React application, and Aspire composes the full local service graph.

## Repository map

- `src/api/Pole.Server`: API composition and process entry point.
- `src/api/Pole.AppHost`: Aspire orchestration for local development.
- `src/api/frontend`: React control-plane UI.
- `src/features`: feature-owned Domain, Application, Contracts, Events, runtime, and test projects.
- `src/shared`: dependency-leaf infrastructure and cross-cutting libraries.
- `tests/Pole.Tests.Architecture`: fast source and boundary checks.
- `tests/Pole.Tests.Integration`: Mongo-backed, API, manifest, and opt-in infrastructure tests.
- `tests/Pole.Tests.E2E`: Aspire and browser end-to-end tests.
- `docs`: canonical architecture, engineering, testing, tenancy, and operations guidance.
- `infrastructure`: Helm and local infrastructure assets.

Start with `docs/README.md`. For implementation rules, read `docs/engineering-guidelines.md`, `docs/module-boundaries.md`, and the focused document for the area being changed.

## Verified commands

The repository pins .NET SDK `11.0.100-preview.7.26381.103` in `global.json`.

From the repository root:

```bash
dotnet restore Pole.slnx --disable-build-servers --nologo -maxcpucount:1 -nodeReuse:false
dotnet build Pole.slnx --no-restore --disable-build-servers --nologo -maxcpucount:1 -nodeReuse:false
```

After that build, independent infrastructure-free suites may run concurrently with `--no-build --no-restore`:

```bash
dotnet test tests/Pole.Tests.Architecture/Pole.Tests.Architecture.csproj --no-build --no-restore --nologo
dotnet test src/features/Functions/Functions.Tests/Functions.Tests.csproj --no-build --no-restore --nologo
dotnet test src/features/GitProviders/GitProviders.Tests/GitProviders.Tests.csproj --no-build --no-restore --nologo
dotnet test src/features/SourceBuilds/SourceBuilds.Tests/SourceBuilds.Tests.csproj --no-build --no-restore --nologo
dotnet test src/shared/Encryption.Tests/Encryption.Tests.csproj --no-build --no-restore --nologo
```

Frontend commands run from `src/api/frontend`:

```bash
npm test
npm run build
npm run lint
```

`npm test` and `npm run build` are currently passing. `npm run lint` is a valid gate but has existing React hook and fast-refresh failures; do not describe it as passing until those are fixed.

Docker is required for `tests/Pole.Tests.Integration`. Aspire E2E and live Kubernetes checks require their documented services and cluster prerequisites. Read `docs/testing.md`, `docs/aspire-local-development.md`, and `docs/aspire-e2e-testing.md`; never claim these lanes passed when their prerequisites were unavailable.

Use `$pole-test-runner` to select the smallest evidence-bearing checks and decide when safe command-level parallelism applies.

## Engineering rules

- Preserve feature ownership: synchronous cross-feature access uses provider-owned Contracts; asynchronous integration uses provider-owned Events.
- Keep one Minimal API endpoint, its request/response types, validator, and Mediator handler together per endpoint file.
- Use typed outcomes for expected failures. Let unexpected infrastructure exceptions reach the standard exception path.
- Tenant-owned behavior must preserve tenant IDs in request markers, filters, events, projections, and frontend headers. Use `$pole-tenant-scoping`.
- Persist mutations and outbox records atomically. Keep external side effects in consumers. Use `$pole-events-outbox`.
- Validate Kubernetes object shape with manifest tests before reaching for live-cluster tests. Use `$pole-kubernetes-resource`.
- Keep frontend API calls centralized in `api.ts` and align `types.ts`, hooks, and active-tenant behavior. Use `$pole-frontend-api`.
- Never print or commit secrets. Keep local credentials in ignored local settings or environment variables.

## Git and delivery

- GitHub Issues is the repository ticket system. The main branch is `master`.
- History is linear and uses concise conventional-style subjects. Preserve that style and avoid merge commits.
- Do not discard, rewrite, or revert changes you did not make. Other agents or the user may be editing the same checkout.
- Do not commit, push, create branches, or open pull requests unless the user explicitly asks for that action.
- When a task branch is authorized, create it after any required plan or diagnosis approval and before implementation changes.
- Check existing issues before creating a new one. Creating issues requires explicit publish intent; drafting does not.
- Local approved implementation plans belong in `.codex/notes/`, which is ignored.

## Agent workflow

- The primary agent owns requirements, decisions, edits, and the final synthesis.
- Do not create persistent project-specific agent personas. Compose a bounded prompt for the current task and use built-in agents only when delegation materially helps.
- Prefer no delegation for ordinary implementation. Use parallel agents for independent, read-heavy exploration, review, or log analysis; use parallel writers only when file ownership is unquestionably disjoint.
- Agents share the checkout and external services. They are not filesystem- or Docker-isolated. Never run concurrent builds or overlapping test projects through separate agents.
- Test parallelism is command-level: build once, then run distinct infrastructure-free projects with `--no-build --no-restore`. Run integration, Aspire, E2E, and live Kubernetes lanes sequentially.
- Before an authorized push or pull request for a non-trivial code change, complete focused tests and self-review, then delegate one independent review to a fresh built-in agent using `$pole-post-task-review`. Do not create a persistent reviewer persona or run multiple reviewers by default.
- Give that reviewer a bounded prompt containing the request or issue, approved plan when present, base branch or commit, exact diff target, test commands and outcomes, skipped checks, and known risks. The reviewer inspects the shared checkout read-only, does not edit or delegate further, and returns prioritized findings with file and line references.
- The primary agent owns triage and fixes, reruns affected checks, and requests at most one further independent pass when review fixes materially change behavior. Skip the independent agent for documentation, skills, agent guidance, or mechanical-only changes unless the user requests it or the change carries unusual risk.

## Completion

- Run the focused checks that prove the changed behavior and report any skipped or unavailable lanes.
- Inspect the actual diff and use `$pole-post-task-review` before handoff.
- Report changed files, checks and outcomes, remaining risks, and any action that still requires user authorization.
