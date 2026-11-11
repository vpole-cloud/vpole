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

The repository pins .NET SDK `11.0.100-rc.3.26522.148` in `global.json`.

From the repository root:

```bash
dotnet restore Pole.slnx --disable-build-servers --nologo -nodeReuse:false
dotnet build Pole.slnx --no-restore --disable-build-servers --nologo -nodeReuse:false
```

The local CLI uses its CPU-based MSBuild worker limit. The gate defaults to two build workers in CI;
`buildMaxCpuCount` can override it. Build once before tests and never overlap a build with tests using
its outputs. Central Integration uses four tenant collections with distinct Mongo containers and
four global collections with a fresh database per case. Process environment mutation has an exclusive
collection. Its xUnit worker limit defaults to four
locally and two in CI; `integrationMaxParallelThreads` overrides that separately from project limits.

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

`npm test`, `npm run build`, and `npm run lint` are required frontend gates. Run them on the current change; do not reuse historical passing results as current evidence.

Docker is required for `tests/Pole.Tests.Integration`. Aspire E2E and live Kubernetes checks require their documented services and cluster prerequisites. Read `docs/testing.md`, `docs/aspire-local-development.md`, and `docs/aspire-e2e-testing.md`; never claim these lanes passed when their prerequisites were unavailable.

Use `$pole-test-runner` for focused development checks. Before opening a PR, run `python3 scripts/quality_gate.py run --profile pr`: every selected test must pass without filters or skips. Run `--profile full` locally before broad changes and through manual CI. Infrastructure unavailability and pre-existing failures block publication. See [agent delivery](docs/agent-delivery.md) for canonical implementations, early slice review and evidence commands.

Standalone C# formatting uses the pinned CleanupCode tool: run `dotnet tool restore` once,
then `python3 scripts/quality_gate.py check-format` or `format`. The shared `Pole Safe Cleanup`
profile enables only C# formatting; the gate checks temporary copies and does not rewrite
sources. CleanupCode needs a .NET 10 runtime in addition to the pinned .NET 11 SDK.
Do not substitute `Pole Full Cleanup` in the gate; it performs semantic changes.

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
- Agents in the same worktree share its files and build outputs. Give independent writers separate worktrees; within one worktree, do not overlap builds or invocations of the same test project.
- Tests in separate worktrees are isolated, including Docker and Kubernetes resources. Docker-backed suites may run concurrently across worktrees. Kubernetes-backed suites may also overlap, but queue them by default because of host load; the primary agent coordinates heavy runs and permits overlap when capacity allows. See [test scheduling](docs/testing.md#parallelism-across-worktrees).
- Within a worktree, build once before parallel `--no-build --no-restore` runs of distinct infrastructure-free projects; use the gate’s configured per-group limits. Kubernetes and E2E each default to one concurrent project.
- For an explicit multi-issue/team-lead delivery workflow, use `$pole-deliver-issues` to coordinate scope, separate worktrees, review, verification and authorized PR/merge actions.
- Before opening any pull request, complete the PR gate with zero failed or skipped selected tests. For broad changes also run the full profile including Kubernetes, Harbor and Aspire E2E. A passing PR profile only proves its selected groups.
- For new features or repeated endpoint families, give agents exact canonical reference files and review one complete representative slice before repeating its pattern. This primary-agent checkpoint does not require another user approval.
- Before an authorized push or pull request for a non-trivial code change, complete the applicable gate profile and self-review, then delegate one independent review to a fresh built-in agent using `$pole-post-task-review`. Do not create a persistent reviewer persona or run multiple reviewers by default.
- Give that reviewer a bounded prompt containing the request or issue, approved plan when present, base branch or commit, exact diff target, test commands and outcomes, skipped checks, and known risks. The reviewer inspects the shared checkout read-only, does not edit or delegate further, and returns prioritized findings with file and line references.
- The primary agent owns triage and fixes, reruns affected checks, and requests at most one further independent pass when review fixes materially change behavior. Skip the independent agent for documentation, skills, agent guidance, or mechanical-only changes unless the user requests it or the change carries unusual risk.

## Completion

- Run focused checks while developing, then the applicable local gate before PR publication. Use `scripts/quality_gate.py record-review`, `verify`, and `open-pr`; do not bypass current evidence with direct PR creation.
- If tests fail, the first sentence of every summary must name the tests and explain why (or state that diagnosis is pending). If suites cannot run, lead with the suite and missing prerequisite. This applies to progress summaries, handoffs, PR descriptions and final answers.
- Turn recurring defects into a focused regression test, semantic rule, formatter rule or corrected canonical example; keep rules precise.
- Inspect the actual diff and use `$pole-post-task-review` before handoff.
- Report changed files, checks and outcomes, remaining risks, and any action that still requires user authorization.
