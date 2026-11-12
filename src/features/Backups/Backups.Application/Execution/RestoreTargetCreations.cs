using MongoDB.Driver;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;

namespace Pole.Features.Backups.Application.Execution;

internal sealed class RestoreTargetCreations(
    IMongoCollection<BackupOperation> operations
)
{
    public async Task<RestoreTargetCreationDecision> RecordAsync(
        RecordRestoreTargetCreationRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("A restore receipt requires the target creation transaction.");

        var authorized = BackupDispatchFilters.RestoreTarget(
            request.TenantId,
            request.OperationId,
            request.ArchiveId,
            request.NewResourceId);
        var result = await operations.UpdateOneAsync(
            session,
            authorized & BackupDispatchFilters.CanCreateRestoreTarget(),
            Builders<BackupOperation>.Update.Set(o => o.RestoreTargetCreated, true),
            cancellationToken: ct);
        if (result.MatchedCount == 1)
            return RestoreTargetCreationDecision.Recorded;
        var created = await operations.Find(session, authorized)
            .Project(o => o.RestoreTargetCreated)
            .SingleOrDefaultAsync(ct);
        return created
            ? RestoreTargetCreationDecision.AlreadyRecorded
            : RestoreTargetCreationDecision.OperationUnavailable;
    }
}
