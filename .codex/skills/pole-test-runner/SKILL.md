---
name: pole-test-runner
description: Test selection and execution workflow for Pole. Use when choosing which tests to add or run, debugging integration test failures, working with Testcontainers, Mongo replica set tests, API slice tests, manifest tests, live Kubernetes tests, or validating changes to backend features, infrastructure managers, observability, search, tenancy, or the frontend build.
---

# Pole Test Runner

Use this skill to choose the cheapest useful validation boundary and run the right commands.

## First Steps

1. Inspect the relevant feature and nearby implementation before editing.
2. Read `docs/testing.md` before changing test strategy.
3. Inspect nearby tests in `tests/Pole.Tests.Integration` before adding new fixtures or helpers.

## Test Boundary

- Prefer integration tests around handlers, mediator flows, Mongo persistence, transactions, pagination, and filtering.
- Use API slice tests when HTTP routing, binding, auth, headers, or result mapping is the behavior under test.
- Use manifest tests for Kubernetes object shape without requiring a live cluster.
- Use live Kubernetes tests only for behavior that cannot be proven through generated manifests or fakes.
- Use focused browser or frontend tests sparingly for key user journeys; do not expand them just to test backend contracts.

## Commands

Run the integration suite from the repo root:

```powershell
dotnet test tests/Pole.Tests.Integration/Pole.Tests.Integration.csproj -maxcpucount:1 -nodeReuse:false
```

Run targeted filters:

```powershell
dotnet test tests/Pole.Tests.Integration/Pole.Tests.Integration.csproj --filter WebServiceKubernetesTests
dotnet test tests/Pole.Tests.Integration/Pole.Tests.Integration.csproj --filter PostgresKubernetesTests
dotnet test tests/Pole.Tests.Integration/Pole.Tests.Integration.csproj --filter RedisKubernetesTests
```

If ingress discovery needs a base URI:

```powershell
$env:POLE_K8S_INGRESS_BASE_URI = 'http://<reachable-ingress-address>'
dotnet test tests/Pole.Tests.Integration/Pole.Tests.Integration.csproj --filter WebServiceKubernetesTests
```

For the frontend:

```powershell
Set-Location src/api/frontend
npm run build
```

## Requirements

- Docker must be running for Testcontainers.
- MongoDB tests require the existing single-node replica set fixture; do not replace it with standalone Mongo.
- Live Kubernetes tests require the current kubeconfig to point at a ready cluster.
- Redis Kubernetes tests may require `POLE_K8S_REDIS_STORAGE_CLASS`.
- Optional local cluster settings can live in `tests/Pole.Tests.Integration/testsettings.local.json`, which is gitignored.

## Adding Tests

- Use factory methods and `TestData` helpers rather than direct object construction.
- Keep infrastructure fakes small and feature-specific.
- Cover cross-tenant and outbox assertions for tenant-owned resource behavior.
- For Kubernetes features, add or update manifest tests first, then add a small live cluster test only when necessary.