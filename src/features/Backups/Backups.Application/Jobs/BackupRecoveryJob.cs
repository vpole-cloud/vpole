// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Hangfire;
using Pole.Features.Backups.Application.Reconciliation;

namespace Pole.Features.Backups.Application.Jobs;

public sealed class BackupRecoveryJob(
    BackupRecoveryScanner scanner
)
{
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public Task RecoverAsync(CancellationToken ct = default) => scanner.RequestDueWorkAsync(ct);
}
