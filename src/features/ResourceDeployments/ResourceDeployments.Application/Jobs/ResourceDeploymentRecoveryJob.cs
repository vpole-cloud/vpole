using Hangfire;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Persistence;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Jobs;

public sealed class ResourceDeploymentRecoveryJob(
    LoadResourceDeploymentRecoveryCandidates loadRecoveryCandidates,
    RecoverResourceDeployment recoverResourceDeployment,
    ResourceDeploymentMetrics metrics,
    ResourceDeploymentSettings settings,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan PendingRecoveryDelay = TimeSpan.FromSeconds(30);

    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var stalePendingBefore = now.Subtract(PendingRecoveryDelay);
        var candidates = await loadRecoveryCandidates(
            now,
            stalePendingBefore,
            settings.RecoveryBatchSize,
            cancellationToken);

        foreach (var deploymentId in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RecoverAsync(deploymentId, now, cancellationToken);
        }
    }

    private async Task<Result> RecoverAsync(
        string deploymentId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var recovery = await recoverResourceDeployment(
            deploymentId,
            now,
            cancellationToken);

        if (!recovery.TryGetValue(out var staleClaimRecoveryCount, out var failure))
            return failure;

        metrics.RecordStaleClaimRecoveries(staleClaimRecoveryCount);
        return Result.Success();
    }

}
