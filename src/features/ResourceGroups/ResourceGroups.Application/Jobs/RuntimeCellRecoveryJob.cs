using Hangfire;
using Pole.Features.ResourceGroups.Application.ResourceGroups;
using Pole.Features.ResourceGroups.Application.RuntimeCells;

namespace Pole.Features.ResourceGroups.Application.Jobs;

public sealed class RuntimeCellRecoveryJob(
    RuntimeCellRecoveryPersistence persistence,
    FinalizeResourceGroupDeletion finalizeResourceGroupDeletion,
    TimeProvider timeProvider
)
{
    private const int BatchSize = 100;
    private static readonly TimeSpan InFlightTimeout = TimeSpan.FromSeconds(30);

    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var staleBefore = now.Subtract(InFlightTimeout);
        var deletingScopes = await persistence.LoadDeletingScopesAsync(BatchSize, cancellationToken);
        foreach (var scope in deletingScopes)
            await finalizeResourceGroupDeletion(scope.TenantId, scope.ResourceGroupId, scope.Id, cancellationToken);

        var candidates = await persistence.LoadRecoveryCandidatesAsync(staleBefore, now, BatchSize, cancellationToken);
        foreach (var candidate in candidates)
            await persistence.RecoverAsync(candidate.Id, now, cancellationToken);
    }
}
