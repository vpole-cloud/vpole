// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.Backups.Application.Reconciliation;

namespace Pole.Features.Backups.Application.Jobs;

public sealed class BackupPolicyJob(
    ApplyBackupPolicy workflow
)
{
    public Task ExecuteAsync(
        string tenantId,
        string policyId,
        CancellationToken ct = default) => workflow.ExecuteAsync(tenantId, policyId, ct);
}
