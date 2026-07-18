# Testing

Pole currently uses a **backend integration-first** testing setup.

The shared integration test suite lives in:

- `tests/Pole.Tests.Integration`

Independently testable feature behavior can live beside the feature in a dedicated test project. The first feature-local suite lives in:

- `src/features/Functions/Functions.Tests`

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
- **MongoDB 8.0** in a disposable container
- **ASP.NET Core TestHost** for API slice tests
- **Kubernetes official C# client** for cluster-backed tests

MongoDB is started as a **single-node replica set** because the application uses Mongo transactions, and transactions require replica set support.

## Test layers

The current suite intentionally mixes a few different test layers inside one project:

- integration tests around handlers, mediator flows, and persistence
- API slice tests through `TestHost`
- manifest tests that validate generated Kubernetes resources without requiring a cluster
- live Kubernetes tests that apply resources to an already-running cluster and verify the result through the Kubernetes API

The default choice should still be the cheapest useful boundary. Most behavior should be covered without needing a live cluster.

For infrastructure-aware features, normal handler and mediator tests should prefer small fakes or in-memory adapters for external systems. The real infrastructure path should then be covered separately by manifest tests and a small number of live cluster tests.

## Requirements

Before running the integration suite:

- make sure **Docker is running**
- make sure the .NET SDK for the repo is installed

If Docker is not running, Testcontainers will fail before tests start.

Before running cluster-backed tests:

- make sure your kubeconfig points to a ready cluster
- make sure required ingress/controllers/CRDs already exist
- optionally set `POLE_K8S_INGRESS_BASE_URI` if ingress is reachable through a stable URI that is not discoverable from resource status
- optionally set `POLE_K8S_REDIS_STORAGE_CLASS` if the cluster does not have a default StorageClass and you want to run Redis Kubernetes tests
- as an alternative, you can create `tests/Pole.Tests.Integration/testsettings.local.json` with a local ingress base URI; this file is gitignored and not required for running tests

## Running tests

Run the feature-local Functions suite from the repository root with:

```bash
dotnet test src/features/Functions/Functions.Tests/Functions.Tests.csproj
```

These tests do not require Docker or the shared integration fixtures.

Run the integration suite from the repository root with:

```bash
dotnet test tests/Pole.Tests.Integration/Pole.Tests.Integration.csproj -maxcpucount:1 -nodeReuse:false
```

The extra flags are useful in constrained or sandboxed environments and are safe to keep for local runs too.

To run only the cluster-backed webservice tests:

```bash
dotnet test tests/Pole.Tests.Integration/Pole.Tests.Integration.csproj --filter WebServiceKubernetesTests
```

To run only the cluster-backed Postgres tests:

```bash
dotnet test tests/Pole.Tests.Integration/Pole.Tests.Integration.csproj --filter PostgresKubernetesTests
```

To run only the cluster-backed Redis tests:

```bash
dotnet test tests/Pole.Tests.Integration/Pole.Tests.Integration.csproj --filter RedisKubernetesTests
```

If ingress discovery needs an explicit base address:

```bash
POLE_K8S_INGRESS_BASE_URI=http://<reachable-ingress-address> dotnet test tests/Pole.Tests.Integration/Pole.Tests.Integration.csproj --filter WebServiceKubernetesTests
```

You can also use an optional local config file:

```json
{
  "POLE_K8S_INGRESS_BASE_URI": "http://<reachable-ingress-address>"
}
```

## Entity construction

Factory methods are the standard way to construct entities in both production code and tests.

Prefer factory methods over direct record or class construction because they:

- keep defaults centralized
- reduce breakage during model refactors
- make tests follow the same creation path as the application
- avoid duplicating lifecycle fields and derived defaults in test code

For generated infrastructure identifiers, prefer shared generators and helpers over hand-built strings.

## Notes

- The current suite is intentionally **not Aspire-based**.
- The default test boundary is integration testing around real Mongo-backed handlers.
- The opt-in Aspire and browser test lane is documented in [Aspire E2E testing](aspire-e2e-testing.md).
- External infrastructure such as Kubernetes is not started by the suite itself; cluster-backed tests assume it already exists.
- Use the full Aspire stack only for production startup wiring and workflows that cross real local services. See [Aspire local development](aspire-local-development.md).

## Harbor contract test

`HarborContractTests` is environment-gated and performs no external I/O unless all four settings are present:

```powershell
$env:POLE_HARBOR_CONTRACT_BASE_URL = 'http://localhost:5000'
$env:POLE_HARBOR_CONTRACT_REGISTRY_HOST = 'host.docker.internal:5000'
$env:POLE_HARBOR_CONTRACT_USERNAME = 'admin'
$env:POLE_HARBOR_CONTRACT_PASSWORD = '<local-admin-password>'
dotnet test tests/Pole.Tests.Integration/Pole.Tests.Integration.csproj --filter Category=HarborContract
```

The test creates a uniquely named private project and build/runtime robot credentials through the real Harbor API, then removes the project in `finally`. Do not place the password in tracked settings or command output.

To also verify a real BuildKit push through the provisioned build robot, keep the Harbor settings above and add:

```powershell
$env:POLE_BUILDKIT_CONTRACT_ADDRESS = 'tcp://localhost:1234'
$env:POLE_BUILDKIT_CONTRACT_PATH = 'C:\path\to\buildctl.exe'
dotnet test tests/Pole.Tests.Integration/Pole.Tests.Integration.csproj --filter Category=HarborBuildKitContract
```

This opt-in contract test builds a hermetic `FROM scratch` image, pushes it to a unique private Harbor project, verifies the digest returned by BuildKit, and removes the project afterward.
