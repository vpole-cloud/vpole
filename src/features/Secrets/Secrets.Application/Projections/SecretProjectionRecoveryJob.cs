using Hangfire;
using Pole.Features.Secrets.Application.Persistence;

namespace Pole.Features.Secrets.Application.Projections;

public sealed class SecretProjectionRecoveryJob(
    LoadSecretProjectionCandidates loadCandidates,
    SecretProjectionReconciler reconciler,
    TimeProvider clock)
{
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task RecoverAsync(CancellationToken ct = default)
    {
        string? cursor = null;
        var deletedAuditBefore = clock.GetUtcNow().UtcDateTime.AddMinutes(-30);
        while (true)
        {
            var candidates = await loadCandidates(cursor, deletedAuditBefore, ct);
            if (candidates.Count == 0)
                return;
            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                await reconciler.ReconcileAsync(candidate.TenantId, candidate.SecretId, ct);
            }
            cursor = candidates[^1].SecretId;
        }
    }
}
