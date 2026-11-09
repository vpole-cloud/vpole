# Testing

Pole currently uses a **backend integration-first** testing setup.

Deployment-target changes should cover bootstrap idempotency and replica races, platform-admin authorization, write-only encrypted credentials, kubeconfig input hardening, two-target client routing, revision/connection-version cache behavior, workload admission, health observation fencing, and service-account metadata tamper resistance. The focused Mongo-backed suite is under `tests/Pole.Tests.Integration/DeploymentTargets`.

Resource placement changes should cover default/additional scope ownership, tenant-safe nested
APIs, atomic scope-target get-or-create, placement-versus-deletion races, generation-fenced
reconciliation, namespace ownership labels, reference registration/removal, retry recovery,
and finalization. The focused slice is under
`tests/Pole.Tests.Integration/ResourceGroups`. Namespace manager tests use fake Kubernetes
HTTP responses; a live cluster is not required for this slice.

Resource registration changes should additionally cover stable identity uniqueness, exact
idempotency, tenant and group isolation, caller-owned transaction rollback, registration-fenced
ResourceGroup deletion, paginated listing, and batched group counts. Each resource provider must
prove registration on create and unregistration on final deletion.

Outbox changes should cover state transitions, required versioned event identities, settings
validation, keyed-write deduplication, concurrent Mongo transactions, publish/retry/abandon
lifecycle, cancellation, and bounded terminal cleanup. The focused tests are under
`tests/Pole.Tests.Integration/Database`, `tests/Pole.Tests.Integration/Outbox`, and
`tests/Pole.Tests.Integration/Events`. Most processor tests use a narrow fake publisher, while
the event-identity contract test uses a disposable RabbitMQ Testcontainer to prove real
publish/consume behavior. Mongo persistence and concurrency tests use the existing replica-set
Testcontainer.

The shared integration test suite lives in:

- `tests/Pole.Tests.Integration`

Independently testable feature behavior can live beside the feature in a dedicated test project. The current feature-local suites live in:

- `src/features/Functions/Functions.Tests`
- `src/features/GitProviders/GitProviders.Tests`
- `src/features/SourceBuilds/SourceBuilds.Tests`
- `src/features/RedisServices/RedisServices.Tests`
- `src/features/RedisServices/RedisServices.Tests.Integration`
- `src/features/RedisServices/RedisServices.Tests.Kubernetes`
- `src/features/Postgres/Postgres.Tests`
- `src/features/Postgres/Postgres.Tests.Integration`
- `src/features/Postgres/Postgres.Tests.Kubernetes`
- `src/features/WebServices/WebServices.Tests` runs the infrastructure-free WebServices checks, including those using a fake Kubernetes client.
- `src/features/WebServices/WebServices.Tests.Integration` starts a Mongo replica set for BSON schema validation, WebService creation, and deployment completion. Create tests send requests through Mediator, substitute the platform Contracts, and verify WebService, release, and outbox writes in Mongo.

## Current approach

Pole uses a **hybrid test layout**. Infrastructure-free feature checks live in sibling `*.Tests`
projects. Feature-owned Mongo and Mediator scenarios live in `*.Tests.Integration`. Central
`Pole.Tests.Integration` retains real cross-feature and HTTP slice guarantees. Live cluster checks
run in `*.Tests.Kubernetes` projects, and full-stack scenarios run in `Pole.Tests.E2E`.

A feature-local test project should reference only the production projects it exercises. Do not reference `Pole.Server`, copy shared integration fixtures, or include test source files from outside the project directory.

Current coverage focuses on:

- handler and mediator flows
- persistence and transaction behavior
- pagination and filtering
- API result-to-HTTP mapping
- manifest generation checks for infrastructure-facing features
- live Kubernetes checks for selected end-to-end resource flows

This keeps tests close to the real application behavior without needing to boot the full local development stack for every test.

## Infrastructure

The integration suite uses:

- **xUnit**
- **Testcontainers**
- **MongoDB 7.0** in a disposable container
- **ASP.NET Core TestHost** for API slice tests
- **Kubernetes official C# client** for cluster-backed tests

MongoDB is started as a **single-node replica set** because the application uses Mongo transactions, and transactions require replica set support.

The integration fixture uses `mongo:7.0` by default. A developer can override only the local
Testcontainers image with `POLE_TEST_MONGO_IMAGE`. This makes MongoDB 7 the standard
integration gate while retaining an explicit MongoDB compatibility check:

```bash
POLE_TEST_MONGO_IMAGE=mongo:8.0 dotnet test tests/Pole.Tests.Integration/Pole.Tests.Integration.csproj -maxcpucount:1 -nodeReuse:false
```

Run that override before claiming MongoDB 8 support, or add it as a separate CI matrix job.

## Test layers

The current suite intentionally mixes a few different test layers inside one project:

- integration tests around handlers, mediator flows, and persistence
- API slice tests through `TestHost`
- manifest tests that validate generated Kubernetes resources without requiring a cluster
- live Kubernetes tests that apply resources to an already-running cluster and verify the result through the Kubernetes API

The default choice should still be the cheapest useful boundary. Most behavior should be covered without needing a live cluster.

Use the [feature test contract](feature-test-contract.md) when planning a feature or material
behavior change so tenant isolation, concurrency, retries, placement, cleanup, and skipped
infrastructure lanes are explicit before implementation.

For infrastructure-aware features, normal handler and mediator tests should prefer small fakes or in-memory adapters for external systems. The real infrastructure path should then be covered separately by manifest tests and a small number of live cluster tests.

## Requirements

Before running the integration suite:

- make sure a **Docker-compatible container runtime** is running (Docker or a configured Podman socket)
- make sure the .NET SDK for the repo is installed

On Linux kernels 6.19 through 7.0.13, MongoDB can be affected by an upstream TCMalloc
incompatibility. Prefer running Mongo-backed tests on kernel 6.18 or 7.0.14+. Use the image
override above when checking a specific MongoDB version.

The lab records its Docker-compatible endpoint in state. Container-bearing test assemblies apply
that endpoint and the Aspire runtime before Testcontainers or Aspire starts, so the separate
`dotnet test` process also uses the Podman socket selected by `lab.sh up`. Explicit Docker,
Testcontainers, and Aspire environment variables remain overrides. If the configured container
runtime is not running, Testcontainers will fail before tests start.

Cluster-backed tests use the reproducible [local Kubernetes test lab](../infrastructure/kubernetes-lab/README.md).
Start the lab once before the backend suite. Linked worktrees discover the primary worktree's
shared default state and target without reading the ambient kubeconfig. `POLE_K8S_LAB_STATE` and
`POLE_K8S_LAB_TARGET` are optional overrides for deliberately non-default lab selections.

## Parallelism across worktrees

Test isolation and host capacity are separate constraints. Independent worktrees have isolated test
resources, including Docker/Testcontainers and Kubernetes resources. Kubernetes namespaces and
container ownership use worktree/run identities; see [E2E isolation and cleanup](aspire-e2e-testing.md#parallelism-and-cleanup).

- **Different worktrees:** Docker-backed suites are relatively light and may run concurrently.
  Kubernetes-backed suites can also overlap safely at the resource-isolation boundary, but are heavy.
  Default to one Kubernetes-backed run at a time, including Aspire E2E/Harbor/BuildKit workflows that
  use the lab. The coordinating agent may allow overlap when current CPU, memory and disk capacity
  support it; reduce concurrency when saturation or contention appears.
- **One worktree:** build outputs are shared. Serialize restore/build against builds and tests using
  those outputs. After building, distinct infrastructure-free projects may run in parallel with
  `--no-build --no-restore`; do not split one project into overlapping invocations. Serialize
  infrastructure lanes according to the configured group limits.
- **Shared lab lifecycle:** existing lifecycle locks and worktree-scoped cleanup still apply. Test
  isolation does not authorize resetting shared services or cleaning another worktree's resources.

The primary agent tracks active heavy runs across its agents. The quality gate uses per-group
project limits and runs groups in sequence. Each selected profile retains complete suite discovery.

## Publication requirement

Before opening a PR, run `python3 scripts/quality_gate.py run --profile pr` on the final
source tree. All selected backend, frontend and automation tests must pass without filters
or skips. Run `--profile full` locally before broad changes and in manual CI. Missing prerequisites
block the affected profile. A PR-profile receipt proves Fast, Services and frontend only.
The gate checks report inventory, formatting and current sources. See
[agent delivery](agent-delivery.md). Summaries of failing tests must identify the affected
tests and reason.

## Running tests

Run the feature-local Functions suite from the repository root with:

```bash
dotnet test src/features/Functions/Functions.Tests/Functions.Tests.csproj
```

Run the GitProviders and SourceBuilds suites the same way:

```bash
dotnet test src/features/GitProviders/GitProviders.Tests/GitProviders.Tests.csproj
dotnet test src/features/SourceBuilds/SourceBuilds.Tests/SourceBuilds.Tests.csproj
```

Run the Redis and PostgreSQL feature suites from the repository root:

```bash
dotnet test src/features/RedisServices/RedisServices.Tests/RedisServices.Tests.csproj
dotnet test src/features/RedisServices/RedisServices.Tests.Integration/RedisServices.Tests.Integration.csproj
dotnet test src/features/Postgres/Postgres.Tests/Postgres.Tests.csproj
dotnet test src/features/Postgres/Postgres.Tests.Integration/Postgres.Tests.Integration.csproj
```

The Functions, GitProviders, SourceBuilds, Redis, and PostgreSQL unit suites do not require Docker or the
shared integration fixtures. The Redis and PostgreSQL integration suites use feature-owned Mongo
fixtures and owner-provided preparation from Tenants.Testing and ResourceGroups.Testing. Their
live Kubernetes cases run in provider-owned *.Tests.Kubernetes projects, all in the Kubernetes group.

Run the architecture boundary checks from the repository root with:

```bash
dotnet test tests/Pole.Tests.Architecture/Pole.Tests.Architecture.csproj
```

This fast suite parses production C# and F# source without starting infrastructure. It protects
removed Mongo and Result APIs, requires named `*Filters` helpers for runtime Mongo filters, requires
closed `*Outcome` types at mediator endpoint boundaries, and verifies feature-owned DI composition and
feature project-reference boundaries. The `POLE001` analyzer enforces the raw Mongo query API
boundary during builds of explicitly migrated C# application projects; equivalent source checks
cover F# application projects in the architecture suite.

Run the integration suite from the repository root with:

```bash
dotnet test tests/Pole.Tests.Integration/Pole.Tests.Integration.csproj -maxcpucount:1 -nodeReuse:false
```

The extra flags are useful in constrained or sandboxed environments and are safe to keep for local runs too.

The normal repository-wide backend workflow is:

```bash
./infrastructure/kubernetes-lab/lab.sh up
./infrastructure/kubernetes-lab/lab.sh verify
dotnet test
```

The default `full` lab supplies every Kubernetes capability used by the solution, including the
three-cluster placement topology, monitoring CRDs, cert-manager, CloudNativePG, Harbor, and
BuildKit. Lab lifecycle remains explicit: `dotnet test` validates and uses an existing lab but
never creates, resets, or deletes one.

## Optional lifecycle profiling

Set `POLE_TEST_TIMING_DIRECTORY` to a fresh artifact directory to record phase durations without
changing test selection. For example, profile the complete gate with:

```bash
POLE_TEST_TIMING_DIRECTORY="$PWD/.artifacts/test-profile/timings" python3 scripts/quality_gate.py run --profile full
```

The shared Mongo fixture records container, replica-set, client, outbox initialization and disposal.
The central Mongo/Mediator/API bases additionally record database preparation, per-case tenant/user
seed, service registration and construction, route mapping, host startup and cleanup. Database
fixtures record migrations once per database and scoped database disposal; GitOps records its cached
immutable catalog preparation. E2E and Kubernetes record named setup, readiness, cleanup action/group
and browser/namespace disposal phases independently of the bounded HTTP request journal. Mongo statistics contain command names,
counts and durations only; command documents, responses and credentials are never recorded. Command
statistics cover these explicit setup/cleanup phases, not requests inside the test method.

Use the gate's timestamped results directory to join the JSONL phases to TRX case identities:

```bash
python3 scripts/summarize_test_timings.py --results-directory .artifacts/quality/RUN --timings-directory .artifacts/test-profile/timings --output .artifacts/test-profile/report
```

The report includes every discovered .NET case and available frontend durations. Detailed phase
coverage is explicit: tests using other fixtures retain TRX timing but may have no setup breakdown.
Fixture phases remain separate from test phases. Unmatched or ambiguous records are reported rather
than attached to a nearby case. Never add overlapping case intervals or Mongo command durations to
wall time. In xUnit 2, reported execution duration excludes `IAsyncLifetime.InitializeAsync` and
`DisposeAsync`; UTC case intervals and explicit phases expose that otherwise missing lifecycle time.

Profiling is opt-in. Unavailable diagnostic storage does not change a test's exception or outcome.

## Central Integration isolation

`Pole.Tests.Integration` gives each persisted tenant scenario an immutable `IntegrationTestScope`:
tenant ID, user ID, email, unique suffix and a concrete `TestDatabaseScope`. Requests and preparation
helpers receive that context explicitly. Tenant and ResourceGroup preparation uses provider-owned
`Tenants.Testing` and `ResourceGroups.Testing`; no ambient current tenant or shared client headers are
switched between tests.

Four explicit xUnit collections each own one Mongo replica set and a shared database. Schema,
indexes and immutable catalogs are prepared once per database. Tenant records remain until fixture
disposal, and every tenant-local query/assertion uses the case's tenant or owned resource identity.
Globally unique routes, hostnames, provider installation identities and document IDs must also be
unique per case; a different TenantId does not partition those indexes.

Catalog mutations, schema/migration tests and database-wide processors use four explicit Global
collections with a dedicated database per case. Their class assignments balance measured setup and
case costs; global work has the same four-local/two-CI worker cap. Process environment mutation uses
an exclusive collection. `MongoIntegrationTestBase` starts with an empty database; isolated
API/Mediator bases prepare an explicitly selected immutable schema profile. Profiles apply complete
owner migration assemblies with the normal ordering and history, and cannot upgrade after creation.
The full profile remains the default for cross-feature scenarios and shared tenant databases;
identity, target and catalog-only cases use narrower profiles. HTTP hosts, DI providers and their mutable
fakes remain per case. Never reset shared storage or switch a fixture's database handle. A narrowly
scoped database-wide assertion may use a local database lease inside an otherwise tenant-owned test.

The committed xUnit runner defaults work with ordinary `dotnet test`. Gate overrides use standard
xUnit RunSettings: four workers locally, two in CI. Collections serialize their own cases and may
overlap other collections up to that limit. Large GitOps scenarios use concrete classes with shared
explicit helper bases rather than one partial class that serializes the whole family. The fixture
contract test checks collection declarations and physical-isolation markers; tracked rename maps
preserve the original case inventory when a class is split.

## Code coverage

Both local and CI profiles require backend coverage. They use the selected project inventory,
bounded executor and built-in Microsoft Cobertura collector. After restoring/building, the backend
lane can run directly. Prepare the full lab when selecting the full profile.

```bash
python3 scripts/quality_gate.py backend --results-directory .artifacts/backend-new-run
```

Choose a fresh directory inside the worktree for every invocation; old manifests and TRX files are
rejected. The executor records each project's command, outcome and coverage files, then rejects
missing, failed, skipped, empty or aborted suites. `python3 scripts/quality_gate.py run` invokes this
same backend lane as part of the complete publication gate.

The lane evaluates actual project roles and assembly names in one MSBuild invocation and writes
`coverage.runsettings` plus `coverage-settings.json`. Only production and migration assemblies are
instrumented; tests, test-support libraries and third-party binaries are excluded. Child-process
coverage stays enabled, including product assemblies started by E2E. The manifest records the
settings hash, included assemblies and observed modules; unexpected modules fail the lane.

Focused development checks may still use plain `dotnet test` without coverage. To collect coverage
for one focused project, generate settings in a fresh directory and add the collector:

```bash
rm -rf -- .artifacts/coverage/backend/raw .artifacts/coverage/backend/report
python3 scripts/quality_gate.py coverage-settings --results-directory .artifacts/coverage/backend/settings
dotnet test src/features/GitProviders/GitProviders.Tests/GitProviders.Tests.csproj \
  --no-build --no-restore --settings .artifacts/coverage/backend/settings/coverage.runsettings \
  --collect:"Code Coverage;Format=Cobertura" \
  --results-directory .artifacts/coverage/backend/raw
```

The repository pins ReportGenerator as a local .NET tool. After collecting one or more backend
Cobertura files, restore the tool and merge them into an HTML report:

```bash
dotnet tool restore
dotnet tool run reportgenerator \
  "-reports:.artifacts/coverage/backend/raw/**/*.cobertura.xml" \
  "-targetdir:.artifacts/coverage/backend/report" \
  "-reporttypes:Html;Cobertura;MarkdownAssembliesSummary" \
  "-filefilters:-**/obj/**;-**/*.g.cs;-**/*.generated.cs"
```

Run frontend tests with V8 coverage using:

```bash
npm run test:coverage --prefix src/api/frontend
```

Its HTML report is written to `src/api/frontend/coverage`. CI collects backend coverage during the
complete backend lane and frontend coverage during the Vitest run, writes separate
summaries to the Actions run page, and uploads both HTML reports as 14-day artifacts. Coverage has
no threshold yet; establish a representative baseline before using it as a merge gate.

Line or branch coverage only shows whether code executed, not whether assertions proved the right
behavior. Coverage also depends on which processes inherit the collector environment; verify
observed product modules before attributing coverage to a separately launched Aspire service.
E2E results remain behavioral evidence even when an external process is absent from coverage.

To run the lab contracts and only the cluster-backed webservice tests:

```bash
dotnet test tests/Pole.Tests.Kubernetes/Pole.Tests.Kubernetes.csproj
dotnet test tests/Pole.Tests.Integration/Pole.Tests.Integration.csproj --filter WebServiceKubernetesTests
```

To run only the cluster-backed Postgres tests:

```bash
dotnet test src/features/Postgres/Postgres.Tests.Integration/Postgres.Tests.Integration.csproj --filter PostgresKubernetesTests
```

To run only the cluster-backed Redis tests:

```bash
dotnet test src/features/RedisServices/RedisServices.Tests.Integration/RedisServices.Tests.Integration.csproj --filter RedisKubernetesTests
```

Smaller `minimal`, `multi`, and `topology` profiles remain available for focused development; see
the lab guide for their capability contract. Plain repository-wide `dotnet test` expects the full
profile. Cluster-independent Kubernetes manifest tests can be selected without a lab:

```bash
dotnet test Pole.slnx --filter Layer=Manifest
```

## Entity construction

Factory methods are the standard way to construct entities in both production code and tests.

Prefer factory methods over direct record or class construction because they:

- keep defaults centralized
- reduce breakage during model refactors
- make tests follow the same creation path as the application
- avoid duplicating lifecycle fields and derived defaults in test code

For generated infrastructure identifiers, prefer shared generators and helpers over hand-built strings.

Tests that create external resources should register cleanup immediately through `TestResourceScope`
instead of maintaining nullable identifiers in a large `finally` block. Execute the test body through
`await resources.RunAsync(async () => { ... });` so a body failure and subsequent cleanup failures
are both preserved. Register restoration before changing shared state, including operations whose
response may be lost after acceptance. Cleanup runs in reverse
registration order, continues after individual failures, and reports them together. Use a cleanup
sequence when one resource has ordered deletion and observation steps that must all be attempted.
Use a concurrent cleanup group only for independent resources such as same-run namespaces on
different lab targets.
Kubernetes cleanup must retain the namespace UID and ownership-label checks; do not register deletion
of shared operators, volumes, ingress controllers, or other lab infrastructure.


## Deterministic E2E contracts

Use `TestWait.UntilAsync` for asynchronous server or Kubernetes probes. It uses a monotonic deadline
and bounds the awaited probe itself; pass its callback token through every HTTP, Kubernetes and
heartbeat request. Use Playwright assertions for visible conditions with a known resource ID,
workspace and section. `NetworkIdle`, option position, global inventory counts and an old Ready
replica do not establish acceptance of the intended operation. Keep real quiet/freshness windows
and the existing latency budgets; a longer timeout is not a synchronization fix.

Bind native readiness to the accepted revision/generation, observed generation, expected spec and
updated/available replicas. Choose profiles and resources by owned IDs, then read back the saved
configuration. Remove owned profiles only after their workloads. Deletion probes accept 404 as
complete and 2xx as pending; unexpected 401/403/5xx terminate that phase with a safe status.

SignalR evidence represents current coverage rather than a union of historical acknowledgements.
Correlate invocation, connection, document and subscription epochs; false/empty acknowledgements
replace previous coverage, and responses after close/unsubscribe cannot restore it. Record HTTP
success, rejection, cancellation and transport failure separately. Intercepted request failures must
fault the phase task immediately, preserving the original exception instead of waiting for a second
timeout.

### Safe failure evidence

`TestResourceScope.RunAsync` activates a bounded `TestPhaseJournal` for the body and cleanup;
`E2eApiClient` records metadata for API attempts in that context. Browser tests create pages through
`BrowserEvidence.NewPageAsync(context)` so the same journal observes page requests until close.
Tests without a resource scope still receive a separate browser journal. Scenario-specific code may
record selection, accepted revision/generation and stream transitions using typed fields. Register
an exact deployment/UID/container request observation before a single Follow action; allow native
actionability waiting to finish before starting the request-completion deadline.

Evidence accepts only phases, monotonic elapsed times, request numbers, HTTP status, typed
revision/generation values supplied by the authoritative parser, and hashed resource/instance/
container/scenario identifiers. It never records credentials, cookies, headers, complete URLs,
arbitrary request/response payloads, exception messages or workload logs. Files have at most 1024
records. Storage failures cannot replace the original scenario failure. A missing evidence file is
a diagnostic gap, never proof that a request succeeded or no request happened.

CI always exports allowlisted TRX outcomes plus JSON phase and headroom summaries, including on
failed runs. Failed outcomes include a fixed error category and up to eight source locations
(tracked repository-relative C#/F# filenames with valid line numbers). Unknown exception types
remain `unknown`; absolute paths, method arguments and arbitrary exception text are discarded.
Known Kubernetes failure status names are mapped to fixed integer HTTP codes; resource-scope
failures also record the typed HTTP status directly, without reading exception messages or bodies.
Raw TRX stdout, stack traces, messages, theory arguments and arbitrary payloads are excluded from
the published failure artifact. Headroom snapshots record free disk/memory and
sanitized node conditions before infrastructure stages and on failure; image-GC warnings remain a
separate infrastructure investigation until linked to a test failure by evidence.

## Notes

- Most test projects do not boot Aspire; only the focused E2E project starts the full service graph.
- The default test boundary is integration testing around real Mongo-backed handlers.
- Aspire and browser coverage is documented in [Aspire E2E testing](aspire-e2e-testing.md) and is
  included by plain `dotnet test` when the full lab is ready.
- The normal test command does not start Kubernetes. Start the reusable lab explicitly first.
- Use the full Aspire stack only for production startup wiring and workflows that cross real local services. See [Aspire local development](aspire-local-development.md).

## Harbor contract test

`HarborContractTests` uses the Harbor service declared by the full lab state. No Harbor or BuildKit
test environment variables are required:

```bash
./infrastructure/kubernetes-lab/lab.sh up
dotnet test tests/Pole.Tests.Integration/Pole.Tests.Integration.csproj --filter Category=HarborContract
```

The test port-forwards the lab service, reads the generated mode-`0600` credential file referenced
by state, creates a uniquely named private project and robot credentials through the real Harbor
API, and registers project deletion with `TestResourceScope`. Credentials are never stored in
tracked settings.

To also verify a real BuildKit push through the provisioned build robot:

```bash
dotnet test tests/Pole.Tests.Integration/Pole.Tests.Integration.csproj --filter Category=HarborBuildKitContract
```

The test uses the lab's pinned `buildctl` and a temporary port-forward to the shared BuildKit
service.

This contract test builds a hermetic `FROM scratch` image, pushes it to a unique private Harbor
project, verifies the digest returned by BuildKit, and removes the project afterward.


## Gate profiles and concurrency

Infrastructure-free tests of CI configuration, lab state parsing and cleanup helpers live in
`tests/Pole.Testing.Tests`. They run in Fast on every PR.

`python3 scripts/quality_gate.py run --profile pr` runs Fast and Services plus frontend.
`--profile full` adds Kubernetes and E2E. CI uses full only for manual workflow_dispatch.
Plain `dotnet test` still discovers all .NET projects in Pole.slnx.

Each group runs with a separate project concurrency limit. Local defaults are Fast 4,
Services 2, Kubernetes 1 and E2E 1. CI defaults are 2, 1, 1 and 1. To override local
limits, create ignored `.quality-gate.local.json`, for example
`{"fast": 8, "services": 2}`, or pass `--config /path/to/limits.json`.
Group order prevents Kubernetes and E2E from competing with other test groups.
Measure project durations and host memory before raising heavy limits.
Restore/build use a single `dotnet` invocation each and let MSBuild schedule the dependency graph.
Locally, `buildMaxCpuCount` defaults to `"auto"` (the SDK's CPU-based limit); CI defaults to 2.
Set a positive integer to cap build workers. The local gate records build duration and sampled
host memory/headroom in its receipt. This is separate from test-project concurrency:
`integrationMaxParallelThreads` bounds central Integration xUnit workers (local 4, CI 2), using the
standard runner setting. Plain `dotnet test` uses the committed xUnit defaults and still runs all
.NET suites. Existing four-key files remain valid; a combined override is
`{"fast": 8, "services": 2, "buildMaxCpuCount": 8, "integrationMaxParallelThreads": 4}`.

The Architecture suite collects fresh project metadata and F# compiler arguments in one MSBuild
engine per test process, preserving imported/conditional properties and references. It does not
restore or compile projects and needs the solution outputs already built. No persisted metadata
cache is trusted. Test output separates input collection time from F# semantic checking.

Build once before tests. The backend manifest records the selected inventory, configuration
and per-project elapsed seconds. Each profile rejects failed, skipped and missing reports
within that inventory.
