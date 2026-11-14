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
