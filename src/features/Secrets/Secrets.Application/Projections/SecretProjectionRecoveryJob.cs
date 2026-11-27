// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Hangfire;

namespace Pole.Features.Secrets.Application.Projections;

public sealed class SecretProjectionRecoveryJob(
    LoadSecretProjectionCandidates loadCandidates,
    SecretProjectionReconciler reconciler,
    TimeProvider clock,
    SecretKeyRetirementReconciler retirement
)
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
                await retirement.ReconcileAsync(candidate.TenantId, candidate.SecretId, ct);
                await reconciler.ReconcileAsync(candidate.TenantId, candidate.SecretId, ct);
            }

            cursor = candidates[^1].SecretId;
        }
    }
}
