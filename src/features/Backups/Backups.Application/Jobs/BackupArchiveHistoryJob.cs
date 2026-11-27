// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.Backups.Application.Archives;

namespace Pole.Features.Backups.Application.Jobs;

public sealed class BackupArchiveHistoryJob(
    ObserveBackupArchiveHistory workflow
)
{
    public Task ExecuteAsync(
        string tenantId,
        string archiveId,
        CancellationToken ct = default) => workflow.ExecuteAsync(tenantId, archiveId, ct);
}
