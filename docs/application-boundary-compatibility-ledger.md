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

No compatibility adapters remain. New cross-feature consumers must use the
provider-owned typed capabilities directly.
