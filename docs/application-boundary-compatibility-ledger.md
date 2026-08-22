# Application Boundary Compatibility Ledger

This ledger records temporary shared-`Result` adapters introduced while feature
boundaries move to provider-owned unions. New consumers must use the canonical
typed delegates. A legacy delegate may be removed as soon as every listed
consumer has completed its owning feature slice.

## ResourceDeployments

| Temporary boundary | Current consumers | Migration owner and checkpoint | Removal condition |
| --- | --- | --- | --- |
| `ResolveResourceDeploymentTargetLegacy` | Postgres create | Postgres, wave 2 | Postgres create consumes `AdmitDeploymentTargetForNewWorkload` directly. |
| `CreateResourceDeploymentLegacy` | Postgres create | Postgres, wave 2 | Postgres request outcome includes `CreateResourceDeploymentError`. |
| `RequestResourceDeploymentApplyLegacy`, `RequestResourceDeploymentDeletionLegacy` | Postgres update/password/delete | Postgres, wave 2 | Postgres request outcomes compose `ResourceDeploymentMutationError`. |
| `LoadCurrentResourceDeploymentComputeLegacy` | Postgres update/password/delete | Postgres, wave 2 | Postgres consumers compose `LoadCurrentResourceDeploymentComputeError`. |
| `EnsureNoUnfinishedResourceGroupDeploymentsLegacy`, `EnsureNoUnfinishedIsolationScopeDeploymentsLegacy` | ResourceGroups group/scope delete and deletion finalizer | ResourceGroups, wave 2 | ResourceGroups outcomes compose the two ResourceDeployments guard errors. |
| `PurgeDeletedResourceDeploymentsLegacy` | Postgres deletion finalization | Postgres, wave 2 | Postgres finalizer composes `ResourceDeploymentFinalizationConflict`. |

The ResourceDeployments slice owns these one-way typed-to-legacy mappings until
their checkpoints. The ledger must be updated in the same commit whenever a
consumer is added, migrated, or removed.

## ComputeProfiles

| Temporary boundary | Current consumers | Migration owner and checkpoint | Removal condition |
| --- | --- | --- | --- |
| `ResolveComputeProfileLegacy` | Postgres create and update | Postgres, wave 2 | Postgres request outcomes compose `ComputeProfileResolutionError`. |

The ComputeProfiles slice owns these one-way typed-to-legacy mappings until
their consumer feature slices complete. New consumers must use the canonical
typed delegates.

## StorageProfiles

| Temporary boundary | Current consumers | Migration owner and checkpoint | Removal condition |
| --- | --- | --- | --- |
| `IStorageProfileResolver` | Postgres create and update | Postgres, wave 2 | Postgres consumes `ResolveStorageProfile` and composes `StorageProfileResolutionError`. |

The StorageProfiles application keeps this one-way compatibility implementation
until Postgres migrates. New consumers must use `ResolveStorageProfile`.
