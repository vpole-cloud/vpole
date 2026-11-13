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
