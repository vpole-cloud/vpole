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
- `src/features/Postgres/Postgres.Tests`
- `src/features/Postgres/Postgres.Tests.Integration`
- `src/features/WebServices/WebServices.Tests` runs the infrastructure-free WebServices checks, including those using a fake Kubernetes client.
- `src/features/WebServices/WebServices.Tests.Integration` starts a Mongo replica set for BSON schema validation and deployment completion, including release and outbox writes.

## Current approach

Pole uses a **hybrid test layout**. Tests that need shared Mongo, mediator, API, consumer, or live infrastructure fixtures remain in `Pole.Tests.Integration`. Tests that exercise one feature without the shared integration harness belong in that feature's sibling `*.Tests` project.

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
  infrastructure lanes within that worktree.
- **Shared lab lifecycle:** existing lifecycle locks and worktree-scoped cleanup still apply. Test
  isolation does not authorize resetting shared services or cleaning another worktree's resources.

The primary agent tracks active heavy runs across its agents. The full quality-gate runner continues
running all projects sequentially; cross-worktree scheduling does not change its completeness or
publication requirements.

## Publication requirement

Before opening any PR, all local backend, frontend and automation tests must pass
on the final source tree without filters or skips. Unavailable Kubernetes, Harbor
or Aspire services block publication; they are not exceptions. This requirement
also applies to documentation/tooling PRs. Run `python3 scripts/quality_gate.py run`
after starting/resuming the full lab. The gate checks test reports, zero skips,
formatting and current source contents; see [agent delivery](agent-delivery.md).
Focused commands below are development tools, not substitutes for this full gate.
Any summary of failing or blocked tests must begin with the affected tests and reason.

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
fixtures; their Kubernetes cases remain separately gated.

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

## Code coverage

Both complete local and CI gates require backend coverage. They use the same discovered project
inventory, sequential executor and built-in Microsoft Cobertura collector. After restoring/building
the solution and preparing the full lab, the backend lane can also run directly:

```bash
python3 scripts/quality_gate.py backend --results-directory .artifacts/backend-new-run
```

Choose a fresh directory inside the worktree for every invocation; old manifests and TRX files are
rejected. The executor records each project's command, outcome and coverage files, then rejects
missing, failed, skipped, empty or aborted suites. `python3 scripts/quality_gate.py run` invokes this
same backend lane as part of the complete publication gate.

Focused development checks may still use plain `dotnet test` without coverage. To collect coverage
for one focused project, choose fresh inputs and add the collector:

```bash
rm -rf -- .artifacts/coverage/backend/raw .artifacts/coverage/backend/report
dotnet test src/features/GitProviders/GitProviders.Tests/GitProviders.Tests.csproj \
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
  "-assemblyfilters:+Pole.*;-*.Tests*;-Pole.Testing;-Pole.ArchitectureAnalyzers" \
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
behavior. The backend collector measures production assemblies loaded inside each test host.
Services launched as separate Aspire processes are not automatically attributed to the E2E test
host, so E2E results remain separate behavioral evidence rather than a coverage metric.

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
