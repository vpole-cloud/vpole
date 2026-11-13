using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Backups.Application.Common;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Domain;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Outbox.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;

namespace Pole.Features.Backups.Application.Destinations;

public sealed record DeleteBackupDestinationCommand(
    string TenantId,
    string Id,
    long ExpectedRevision
);

public sealed record BackupDestinationDeleted;

public sealed record BackupDestinationInUse;

public sealed record BackupDestinationCleanupPending;

public union DeleteAcceptedBackupDestinationOutcome(
    BackupDestinationDeleted,
    BackupDestinationMissing,
    BackupDestinationRevisionConflict,
    BackupDestinationInUse,
    BackupDestinationCleanupPending,
    BackupDestinationRegistrationConflict);

public union DeleteManualBackupDestinationOutcome(
    BackupDestinationDeleted,
    BackupDestinationMissing,
    BackupDestinationRevisionConflict,
    BackupDestinationInUse,
    BackupDestinationCleanupPending,
    BackupDestinationRegistrationConflict,
    BackupDestinationGitOwned);

public sealed class DeleteBackupDestination(
    IMongoCollection<BackupDestination> destinations,
    IMongoCollection<BackupArchive> archives,
    MongoRunTransaction transactions,
    IGitOpsMutations git,
    StoreOutboxWrites outbox,
    IResourceRegistrar registrar,
    GetResourceDeploymentFinalizationReadiness readiness,
    PurgeDeletedResourceDeployments purge
)
{
    public Task<DeleteManualBackupDestinationOutcome> ExecuteAsync(
        DeleteBackupDestinationCommand command,
        CancellationToken ct) => transactions.InvokeOutcomeAsync<DeleteManualBackupDestinationOutcome>(
        async (transaction, token) =>
        {
            if (!await git.AuthorizeAsync(
                    new(command.TenantId, BackupDeclarationKinds.Destination, command.Id),
                    transaction.Session,
                    token))
                return transaction.Abort(new DeleteManualBackupDestinationOutcome(new BackupDestinationGitOwned()));

            var accepted = await ExecuteAcceptedAsync(command, transaction.Session, token);
            DeleteManualBackupDestinationOutcome result = accepted switch
            {
                BackupDestinationDeleted success => success,
                BackupDestinationMissing error => error,
                BackupDestinationRevisionConflict error => error,
                BackupDestinationInUse error => error,
                BackupDestinationCleanupPending error => error,
                BackupDestinationRegistrationConflict error => error,
            };
            return result is BackupDestinationDeleted ? transaction.Commit(result) : transaction.Abort(result);
        },
        ct);

    public async Task<DeleteAcceptedBackupDestinationOutcome> ExecuteAcceptedAsync(
        DeleteBackupDestinationCommand command,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var current = await destinations
            .Find(session, BackupsFilters<BackupDestination>.ById(command.TenantId, command.Id))
            .SingleOrDefaultAsync(ct);
        if (current is null)
            return new BackupDestinationMissing();
        if (current.Revision != command.ExpectedRevision)
            return new BackupDestinationRevisionConflict();
        if (await archives.Find(session, BackupDispatchFilters.DestinationArchives(command.TenantId, command.Id))
                .AnyAsync(ct))
            return new BackupDestinationInUse();

        var state = await readiness(new(command.TenantId, BackupResourceTypes.Destination, command.Id), session, ct);
        switch (state)
        {
            case ResourceDeploymentFinalizationReadiness.Waiting:
                return new BackupDestinationCleanupPending();
            case ResourceDeploymentFinalizationReadiness.Ready:
                if (await purge(
                        new(command.TenantId, BackupResourceTypes.Destination, command.Id),
                        session,
                        ct) is ResourceDeploymentFinalizationConflict)
                    return new BackupDestinationCleanupPending();
                break;
            case ResourceDeploymentFinalizationReadiness.Missing:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (await registrar.UnregisterAsync(
                new(command.TenantId, current.ResourceGroupId, BackupResourceTypes.Destination, command.Id),
                session,
                ct) is not ResourceUnregistered)
            return new BackupDestinationRegistrationConflict();

        var deleted = await destinations.DeleteOneAsync(
            session,
            BackupConfigurationFilters<BackupDestination>.AtRevision(
                command.TenantId,
                command.Id,
                command.ExpectedRevision),
            cancellationToken: ct);
        if (deleted.DeletedCount != 1)
            return new BackupDestinationRevisionConflict();

        await outbox(session, [BackupConfigurationNotifications.DestinationRemoved(current)], ct);
        return new BackupDestinationDeleted();
    }
}
