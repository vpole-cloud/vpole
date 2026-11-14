using MongoDB.Driver;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Domain;

namespace Pole.Features.Backups.Application.Operations;

public sealed record LoadBackupOperationRequest(
    string TenantId,
    string Id
);

public sealed record BackupOperationMissing;

public union LoadBackupOperationOutcome(BackupOperation, BackupOperationMissing);

public sealed class LoadBackupOperation(
    IMongoCollection<BackupOperation> operations
)
{
    public async Task<LoadBackupOperationOutcome> ExecuteAsync(LoadBackupOperationRequest request, CancellationToken ct)
    {
        var operation = await operations.Find(BackupsFilters<BackupOperation>.ById(request.TenantId, request.Id))
            .SingleOrDefaultAsync(ct);
        return operation is null ? new BackupOperationMissing() : operation;
    }
}
