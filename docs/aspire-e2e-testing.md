# Aspire E2E Testing

`tests/Pole.Tests.E2E` is an opt-in full-stack test lane. It starts an isolated Aspire graph with disposable MongoDB, Keycloak, and RabbitMQ containers; real Pole API/frontend processes; and a local GitHub protocol stub. It does not reuse local-development ports or data volumes.

The focused coverage is intentionally small:

- real Keycloak login, JWT validation, realm roles, and the platform-admin user-creation policy;
- asynchronous default-tenant creation through MongoDB transactions, the outbox, and RabbitMQ;
- tenant isolation before a GitHub provider request is made;
- GitHub App installation and OAuth callback redirects against the local stub;
- browser login, explicit tenant selection, and resource-group creation at desktop and mobile viewports.

The GitHub stub serves deterministic `v1` and `v2` source archives and exposes protected reset, revision, and sanitized request-journal routes. It never records bearer tokens, OAuth codes, callback state, private keys, or client secrets.

## Prerequisites

- Docker Desktop or a Docker Engine that Aspire/DCP can use.
- .NET SDK 10 and Node dependencies for `src/api/frontend`.
- A local Kubernetes context and `kubectl` if exercising the resource-group reconciliation side effect.
- Playwright Chromium installed for the browser test.

Install frontend and browser dependencies once:

```powershell
npm ci --prefix src/api/frontend
pwsh tests/Pole.Tests.E2E/bin/Debug/net10.0/playwright.ps1 install chromium
```

Build the test project first if the Playwright script has not been generated yet.

## Run

Run only this opt-in lane from the repository root:

```powershell
dotnet test tests/Pole.Tests.E2E/Pole.Tests.E2E.csproj --no-restore -maxcpucount:1
```

Do not run it beside a manually started AppHost that shares Docker, Keycloak, or Kubernetes resources. The fixture creates a unique Keycloak bootstrap user, two test users, a direct-grant client, a temporary RSA key, and a dynamic frontend origin. It restores the UI client configuration, deletes temporary Keycloak principals/client, and disposes the entire AppHost graph in reverse cleanup order.

The test results need no user secrets and never call the real GitHub API.

## Source-build lane

Full source-build verification remains an explicitly prepared, manual lane because Harbor owns a fixed Compose topology and BuildKit requires a privileged container. Prepare Harbor, `buildctl`, Docker registry trust, and Kubernetes using [GitHub source build E2E](github-source-build-e2e.md). Keep that lane serial and on an isolated self-hosted runner; do not add Harbor credentials or generated Harbor files to repository settings.

The core Aspire E2E lane validates the source provider contract with deterministic repository revisions. Set the following process-only settings to enable `SourceBuildE2ETests`:

```powershell
$env:POLE_E2E_SOURCE_BUILDS = '1'
$env:POLE_E2E_HARBOR_COMPOSE_DIR = 'C:\path\to\prepared-harbor'
$env:POLE_E2E_HARBOR_ADMIN_PASSWORD = '<local-admin-password>'
$env:POLE_E2E_BUILDCTL_PATH = 'C:\path\to\buildctl.exe'
# Optional when the local topology uses the defaults:
$env:POLE_E2E_HARBOR_BASE_URL = 'http://localhost:5000'
$env:POLE_E2E_HARBOR_REGISTRY_HOST = 'host.docker.internal:5000'

dotnet test tests/Pole.Tests.E2E/Pole.Tests.E2E.csproj --filter SourceBuildE2ETests -maxcpucount:1
```

That test starts Harbor and BuildKit through Aspire, verifies the exact source commit, Harbor artifact digest, source-build release provenance, and a BuildKit failure path. It then removes the source-build configuration, source repository, web service, resource group/namespace, Harbor project, workspace, and the Compose resources. The Kubernetes context must be authorized to create and delete the temporary namespace.

## CI

The workflow [aspire-e2e.yml](../.github/workflows/aspire-e2e.yml) is manually dispatched on a prepared self-hosted runner. It is intentionally separate from the fast integration-test workflow. A source-build job should be enabled only after the runner has the Harbor and Kubernetes prerequisites above.
