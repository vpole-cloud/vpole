# Application Boundary Compatibility Ledger

This ledger records temporary shared-`Result` adapters introduced while feature
boundaries move to provider-owned unions. New consumers must use the canonical
typed delegates. A legacy delegate may be removed as soon as every listed
consumer has completed its owning feature slice.

## ResourceDeployments

| Temporary boundary | Current consumers | Migration owner and checkpoint | Removal condition |
| --- | --- | --- | --- |
| `EnsureNoUnfinishedResourceGroupDeploymentsLegacy`, `EnsureNoUnfinishedIsolationScopeDeploymentsLegacy` | ResourceGroups group/scope delete and deletion finalizer | ResourceGroups, wave 2 | ResourceGroups outcomes compose the two ResourceDeployments guard errors. |

The ResourceDeployments slice owns these one-way typed-to-legacy mappings until
their checkpoints. The ledger must be updated in the same commit whenever a
consumer is added, migrated, or removed.

## GitProviders

| Temporary boundary | Current consumers | Migration owner and checkpoint | Removal condition |
| --- | --- | --- | --- |
| `ISourceRepositoryBuildAccess` returning legacy `Result` | SourceBuild configuration, job, and workspace flows | SourceBuilds, wave 3 | SourceBuilds consumes typed `FindSourceRepositoryBuild` and `DownloadSourceRepositoryArchive` capabilities. |

`SourceRepositoryBuildAccess` is the only typed-to-legacy adapter in
GitProviders. Its local persistence and GitHub provider operations already use
the canonical typed capabilities; no new consumer may depend on the legacy
interface.

## ContainerRegistries

| Temporary boundary | Current consumers | Migration owner and checkpoint | Removal condition |
| --- | --- | --- | --- |
| `IContainerRegistryBuildService` returning legacy `Result` | SourceBuild job executor | SourceBuilds, wave 3 | SourceBuilds consumes `PrepareRegistryBuildPush` and `GetTenantRegistryArtifact` directly. |

`SourceBuildContainerRegistryBuildService` is the only typed-to-legacy adapter
in ContainerRegistries. WebServices already consumes
`ResolveRuntimeRegistryAccess`; no new consumer may depend on the legacy build
interface.
