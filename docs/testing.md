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

This keeps tests close to the real application behavior without needing to boot the full local development stack for every test.

## Infrastructure

The integration suite uses:

- **xUnit**
- **Testcontainers**
- **MongoDB 8.0** in a disposable container
- **ASP.NET Core TestHost** for API slice tests

MongoDB is started as a **single-node replica set** because the application uses Mongo transactions, and transactions require replica set support.

## Requirements

Before running the integration suite:

- make sure **Docker is running**
- make sure the .NET SDK for the repo is installed

If Docker is not running, Testcontainers will fail before tests start.

## Running tests

Run the integration suite from the repository root with:

```bash
dotnet test tests/Pole.Tests.Integration/Pole.Tests.Integration.csproj -maxcpucount:1 -nodeReuse:false
```

The extra flags are useful in constrained or sandboxed environments and are safe to keep for local runs too.

## Notes

- The current suite is intentionally **not Aspire-based**.
- The default test boundary is integration testing around real Mongo-backed handlers.
- Full browser e2e coverage is planned to stay small and focused on key user journeys.
- External infrastructure such as Kubernetes is not started in the integration suite; tests isolate those side effects where possible.
