# Application Boundary Compatibility Ledger

This ledger records temporary shared-`Result` adapters introduced while feature
boundaries move to provider-owned unions. New consumers must use the canonical
typed delegates. A legacy delegate may be removed as soon as every listed
consumer has completed its owning feature slice.

## ResourceDeployments

| Temporary boundary | Current consumers | Migration owner and checkpoint | Removal condition |
| --- | --- | --- | --- |
| `ResolveResourceDeploymentTargetLegacy` | RedisServices create; Postgres create | DeploymentTargets admission plus RedisServices and Postgres, waves 1-2 | Both create flows consume the DeploymentTargets provider union directly. |
| `CreateResourceDeploymentSetLegacy` | WebServices create | WebServices, wave 1 | CreateWebService exposes `CreateResourceDeploymentError` in its request outcome. |
| `CreateResourceDeploymentLegacy` | RedisServices create; Postgres create | RedisServices and Postgres, wave 2 | Both request outcomes include `CreateResourceDeploymentError`. |
| `RequestResourceDeploymentSetApplyLegacy`, `RequestResourceDeploymentSetDeletionLegacy`, `RequestExactResourceDeploymentApplyLegacy`, `RequestExactResourceDeploymentDeletionLegacy`, `RetryExactResourceDeploymentLegacy`, `AdvanceResourceDeploymentRevisionLegacy` | WebServices update, release, deployment, hostname, retry, delete, and source-build flows | WebServices, wave 1 | Every listed handler/service composes the relevant ResourceDeployments error union and aborts its caller-owned transaction explicitly. |
| `RequestResourceDeploymentApplyLegacy`, `RequestResourceDeploymentDeletionLegacy` | RedisServices update/delete; Postgres update/password/delete | RedisServices and Postgres, wave 2 | The RedisServices and Postgres request outcomes compose `ResourceDeploymentMutationError`. |
| `LoadCurrentResourceDeploymentComputeLegacy` | RedisServices update/delete; Postgres update/password/delete | RedisServices and Postgres, wave 2 | Consumers compose `LoadCurrentResourceDeploymentComputeError`. |
| `EnsureComputeProfileUnreferencedLegacy` | ComputeProfiles delete | ComputeProfiles, wave 1 | DeleteComputeProfile composes `ComputeProfileReferencedByResourceDeployment`. |
| `EnsureNoUnfinishedResourceGroupDeploymentsLegacy`, `EnsureNoUnfinishedIsolationScopeDeploymentsLegacy` | ResourceGroups group/scope delete and deletion finalizer | ResourceGroups, wave 2 | ResourceGroups outcomes compose the two ResourceDeployments guard errors. |
| `PurgeDeletedResourceDeploymentsLegacy`, `PurgeExactDeletedResourceDeploymentLegacy` | WebServices, RedisServices, and Postgres deletion finalization | WebServices wave 1; RedisServices and Postgres wave 2 | Each finalizer composes `ResourceDeploymentFinalizationConflict`; remove the exact adapter after WebServices and the set adapter after all three consumers migrate. |

The ResourceDeployments slice owns these one-way typed-to-legacy mappings until
their checkpoints. The ledger must be updated in the same commit whenever a
consumer is added, migrated, or removed.
