// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.Backups.Application.Operations;

namespace Pole.Features.Backups.Application.Jobs;

public sealed class BackupOperationJob(
    ExecuteBackupOperation workflow
)
{
    public Task ExecuteAsync(
        string tenantId,
        string operationId,
        CancellationToken ct = default) => workflow.ExecuteAsync(tenantId, operationId, ct);
}
