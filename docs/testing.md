# Testing

Pole currently uses a **backend integration-first** testing setup.

The main automated test suite lives in:

- `tests/Pole.Tests.Integration`

## Current approach

The test suite is organized as **one shared test project** with folders per feature instead of separate test projects for each feature.

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
- as an alternative, you can create `tests/Pole.Tests.Integration/testsettings.local.json` with a local ingress base URI; this file is gitignored and not required for running tests

## Running tests

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
- Full browser e2e coverage is planned to stay small and focused on key user journeys.
- External infrastructure such as Kubernetes is not started by the suite itself; cluster-backed tests assume it already exists.
