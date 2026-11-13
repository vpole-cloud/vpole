using MongoDB.Driver;
using Pole.Features.Backups.Application.Archives;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Application.Reconciliation;
using Pole.Features.Backups.Domain;
using Pole.Features.Outbox.Contracts;

namespace Pole.Features.Backups.Application.Operations;

public sealed class ScheduledBackupHistory(
    IMongoCollection<BackupOperation> operations,
    StoreOutboxWrites outbox,
    TimeProvider clock
)
{
    internal async Task InterruptAsync(
        string tenantId,
        string archiveId,
        ScheduledObservationInterruption interruption,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var running = BackupDispatchFilters.RunningScheduled(tenantId, archiveId);
        var reason = interruption switch
        {
            ScheduledObservationInterruption.SourceRemoved => "source-removed-before-final-observation",
            ScheduledObservationInterruption.ArchiveRetired => "archive-retired-before-final-observation",
            _ => throw new ArgumentOutOfRangeException(nameof(interruption)),
        };
        foreach (var operation in await operations.Find(session, running).ToListAsync(ct))
        {
            var interrupted = operation.InterruptObservation(reason, clock.GetUtcNow().UtcDateTime);
            await operations.ReplaceOneAsync(
                session,
                BackupsFilters<BackupOperation>.ById(tenantId, operation.Id),
                interrupted,
                cancellationToken: ct);
            await outbox(session, [BackupWorkRequests.OperationChanged(interrupted)], ct);
        }
    }
}
