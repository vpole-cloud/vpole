using Hangfire;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Persistence;
using Pole.Features.ResourceDeployments.Application.Persistence.Reads;
using Pole.Features.ResourceDeployments.Application.Persistence.Reconciliation;

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
            await RecoverOneAsync(deploymentId, now, cancellationToken);
        }
    }

    private async Task RecoverOneAsync(
        string deploymentId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var staleClaimRecoveryCount = await recoverResourceDeployment(
            deploymentId,
            now,
            cancellationToken);

        metrics.RecordStaleClaimRecoveries(staleClaimRecoveryCount);
    }

}
