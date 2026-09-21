# Local Skrynia provider validation

This experimental worktree can run Pole.Server with the local Skrynia development package. Application persistence, tenant filters, transactions and outbox code still use MongoDB.Driver. MongoDB remains the default provider.

## Package and startup configuration

The root NuGet.Config maps only `Skrynia` to the sibling `../skrynia-driver-compat/artifacts/packages` feed. Build that package first, or supply the configured development version in that feed. `SkryniaPackageVersion` in Directory.Packages.props selects the version; override it consistently for restore/build when testing another package.

Configure the composition root:

```json
{
  "Database": {
    "Provider": "Skrynia",
    "FilePath": "/absolute/local/path/pole.sqlite"
  }
}
```

The path must be absolute and reside on a supported local filesystem (not tmpfs). One process owns the file, including all logical Pole, Orleans and Hangfire databases. DI owns the Skrynia client until host shutdown. Readiness includes an actual database read. Normal migrations, deployment-target bootstrap, background workers, authentication and RabbitMQ remain enabled.

Hangfire uses polling, no capped collection and no Mongo collection-clone backup; use Skrynia's coordinated backup API operationally. Orleans uses SingleDocument membership with the existing Pole grain serializer. This is a single-process profile, not a multi-replica Pole deployment or a claim of all Orleans functionality.

The AppHost reads the same Database settings, passes them to Pole.Server and omits all MongoDB resources when Skrynia is selected. Existing Aspire encryption-key and service configuration prerequisites still apply. From the worktree root:

```bash
Database__Provider=Skrynia Database__FilePath=/absolute/local/path/pole.sqlite ASPIRE_CONTAINER_RUNTIME=podman aspire start --apphost src/api/Pole.AppHost/Pole.AppHost.csproj
```

## Automated isolated verification

Use the pinned SDK and the usual sequential restore/build commands. The E2E fixture generates its own temporary keys and identities, uses disposable Keycloak/RabbitMQ resources, and creates its SQLite file under the test output directory. It uses the reusable full Kubernetes lab for bootstrap, Harbor and BuildKit, plus frontend dependencies and the pinned Playwright container. Follow [the E2E prerequisites](aspire-e2e-testing.md); a kubeconfig alone is insufficient.

```bash
dotnet restore Pole.slnx --configfile NuGet.Config --disable-build-servers --nologo -maxcpucount:1 -nodeReuse:false
dotnet build Pole.slnx --no-restore --disable-build-servers --nologo -maxcpucount:1 -nodeReuse:false
./infrastructure/kubernetes-lab/lab.sh verify
POLE_E2E_DATABASE_PROVIDER=Skrynia ASPIRE_CONTAINER_RUNTIME=podman dotnet test tests/Pole.Tests.E2E/Pole.Tests.E2E.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~AuthenticationAndGitHubE2ETests|FullyQualifiedName~PersistenceE2ETests|FullyQualifiedName~BrowserSmokeE2ETests'
```

The persistence scenario exercises API create/list/update/delete, cross-tenant isolation, outbox publication and the resulting search projection, then stops/reopens/restarts the real server and verifies durable state. The fixture opens the SQLite file for inspection only after the server has stopped; assertions use driver interfaces. If publication has not yet reached the search consumer, the bounded test restarts workers and retries inspection until this resource's projected revision is committed.

The standalone `tests/Pole.Tests.Skrynia` project also exercises production migrations, MigrationRunner and transaction behavior without containers. See its README for commands.

## Upgrade boundary

The new Skrynia candidate uses file format 4 for partial-index predicates. Opening older supported files upgrades them transactionally. Back up before trying it on retained data; older Skrynia binaries cannot reopen format 4. These changes and successful focused tests do not constitute production qualification or complete Pole feature coverage. The local package has not been published.
