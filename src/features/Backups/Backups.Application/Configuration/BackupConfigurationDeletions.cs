using MongoDB.Driver;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Domain;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;

namespace Pole.Features.Backups.Application.Configuration;

public sealed class BackupConfigurationDeletions(
    IMongoCollection<BackupDestination> destinations,
    IMongoCollection<BackupPolicy> policies,
    IMongoCollection<BackupArchive> archives,
    BackupConfigurationWriter writer,
    IResourceRegistrar registrar,
    GetResourceDeploymentFinalizationReadiness readiness,
    PurgeDeletedResourceDeployments purge,
    TimeProvider clock
)
{
    public async Task<BackupWriteResult<bool>> DeletePolicyAsync(
        string tenantId,
        string id,
        long expectedRevision,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var filter = BackupsFilters<BackupPolicy>.ById(tenantId, id);
        var current = await policies.Find(session, filter).SingleOrDefaultAsync(ct);
        if (current is null)
            return new BackupWriteResult<bool>.Rejected("not-found");
        if (current.Revision != expectedRevision)
            return new BackupWriteResult<bool>.Rejected("revision-conflict");
        if (!current.Deleting)
        {
            var eventCore = EventCore.Create(tenantId);
            var retiring = current.BeginStopping(
                new BackupExecutionOrigin(eventCore.TraceId, eventCore.CorrelationId),
                clock.GetUtcNow().UtcDateTime);
            await policies.ReplaceOneAsync(session, filter, retiring, cancellationToken: ct);
            await writer.PolicyChanged(session, retiring, ct);
        }

        return new BackupWriteResult<bool>.Saved(true);
    }

    public async Task<BackupWriteResult<bool>> DeleteDestinationAsync(
        string tenantId,
        string id,
        long expectedRevision,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var filter = BackupsFilters<BackupDestination>.ById(tenantId, id);
        var current = await destinations.Find(session, filter).SingleOrDefaultAsync(ct);
        if (current is null)
            return new BackupWriteResult<bool>.Rejected("not-found");
        if (current.Revision != expectedRevision)
            return new BackupWriteResult<bool>.Rejected("revision-conflict");
        if (await archives.Find(session, BackupDispatchFilters.DestinationArchives(tenantId, id)).AnyAsync(ct))
            return new BackupWriteResult<bool>.Rejected("destination-in-use");
        var state = await readiness(new(tenantId, BackupConfigurationWriter.DestinationType, id), session, ct);
        if (state == ResourceDeploymentFinalizationReadiness.Waiting ||
            state == ResourceDeploymentFinalizationReadiness.Ready && await purge(
                new(tenantId, BackupConfigurationWriter.DestinationType, id),
                session,
                ct) is ResourceDeploymentFinalizationConflict)
            return new BackupWriteResult<bool>.Rejected("destination-cleanup-pending");
        if (await registrar.UnregisterAsync(
                new(tenantId, current.ResourceGroupId, BackupConfigurationWriter.DestinationType, id),
                session,
                ct) is not ResourceUnregistered)
            return new BackupWriteResult<bool>.Rejected("registration-conflict");
        await destinations.DeleteOneAsync(session, filter, cancellationToken: ct);
        await writer.Changed(session, current, "BackupDestination", current.Revision + 1, ct);
        return new BackupWriteResult<bool>.Saved(true);
    }
}
