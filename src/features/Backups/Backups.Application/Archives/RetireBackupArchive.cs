using MongoDB.Driver;
using Pole.Features.Backups.Application.Execution;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Domain;

namespace Pole.Features.Backups.Application.Archives;

public closed record RetireBackupArchiveRequest(
    BackupPolicy Policy
);

public sealed record RetireRemovedBackupSource(
    BackupPolicy Policy
) : RetireBackupArchiveRequest(Policy);

public sealed record RetireStoppedBackupPolicy(
    BackupPolicy Policy
) : RetireBackupArchiveRequest(Policy);

public sealed record RetireReplacedBackupArchive(
    BackupPolicy Policy,
    BackupArchive Replacement
) : RetireBackupArchiveRequest(Policy);

public enum ScheduledObservationInterruption { SourceRemoved, ArchiveRetired }

public sealed class RetireBackupArchive(
    IMongoCollection<BackupArchive> archives,
    BackupOperations operations
)
{
    public async Task ExecuteAsync(
        RetireBackupArchiveRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var policy = request.Policy;
        var filter = BackupsFilters<BackupArchive>.ById(policy.TenantId, policy.ArchiveId);
        var current = await archives.Find(session, filter).SingleAsync(ct);
        var retired = request switch
        {
            RetireRemovedBackupSource or RetireStoppedBackupPolicy => current.Retire(),
            RetireReplacedBackupArchive replacement => current.RetireForReplacement(replacement.Replacement),
        };
        await archives.ReplaceOneAsync(session, filter, retired, cancellationToken: ct);

        var reason = request switch
        {
            RetireRemovedBackupSource => ScheduledObservationInterruption.SourceRemoved,
            RetireStoppedBackupPolicy or RetireReplacedBackupArchive => ScheduledObservationInterruption.ArchiveRetired,
        };
        await operations.InterruptScheduledObservationsAsync(policy.TenantId, policy.ArchiveId, reason, session, ct);
    }
}
