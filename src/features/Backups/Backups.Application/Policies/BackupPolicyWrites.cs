using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Backups.Application.Archives;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Application.Providers;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Features.Events.Abstractions;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Outbox.Contracts;
using Pole.Scheduling;

namespace Pole.Features.Backups.Application.Policies;

public sealed record CreateBackupPolicyCommand(
    string TenantId,
    string Id,
    BackupPolicyInput Input
);

public sealed record UpdateBackupPolicyCommand(
    string TenantId,
    string Id,
    long ExpectedRevision,
    BackupPolicyInput Input
);

public sealed record BackupPolicyMissing;

public sealed record BackupPolicyRevisionConflict;

public sealed record BackupPolicyIdentityImmutable;

public sealed record BackupPolicyStopping;

public sealed record BackupPolicyGitOwned;

public sealed record BackupPolicyNameAlreadyExists;

public sealed record BackupPolicySourceAlreadyBound;

public sealed record BackupPolicyDestinationUnavailable;

public union CreateBackupPolicyWriteOutcome(
    BackupPolicy,
    BackupPolicyNameAlreadyExists,
    BackupPolicySourceAlreadyBound,
    BackupPolicyDestinationUnavailable,
    BackupSourceUnavailable,
    BackupSourceUnsupported,
    BackupSourcePlacementUnavailable,
    BackupSourceRevisionChanged,
    BackupDestinationPlacementUnavailable,
    BackupRetentionInvalid);

public union UpdateAcceptedBackupPolicyOutcome(
    BackupPolicy,
    BackupPolicyMissing,
    BackupPolicyRevisionConflict,
    BackupPolicyIdentityImmutable,
    BackupPolicyStopping,
    BackupPolicyDestinationUnavailable,
    BackupSourceUnavailable,
    BackupSourceUnsupported,
    BackupSourcePlacementUnavailable,
    BackupSourceRevisionChanged,
    BackupDestinationPlacementUnavailable,
    BackupRetentionInvalid);

public union UpdateManualBackupPolicyOutcome(
    BackupPolicy,
    BackupPolicyMissing,
    BackupPolicyRevisionConflict,
    BackupPolicyIdentityImmutable,
    BackupPolicyStopping,
    BackupPolicyGitOwned,
    BackupPolicyDestinationUnavailable,
    BackupSourceUnavailable,
    BackupSourceUnsupported,
    BackupSourcePlacementUnavailable,
    BackupSourceRevisionChanged,
    BackupDestinationPlacementUnavailable,
    BackupRetentionInvalid);

public sealed class BackupPolicyWrites(
    IMongoCollection<BackupPolicy> policies,
    IMongoCollection<BackupDestination> destinations,
    BackupProviderCatalog providers,
    CreateBackupArchive createArchive,
    RetireBackupArchive retireArchive,
    MongoRunTransaction transactions,
    IGitOpsMutations git,
    StoreOutboxWrites outbox,
    TimeProvider clock
)
{
    public Task<CreateBackupPolicyWriteOutcome> CreateAsync(CreateBackupPolicyCommand command, CancellationToken ct) =>
        transactions.InvokeOutcomeAsync<CreateBackupPolicyWriteOutcome>(
            async (transaction, token) =>
            {
                var result = await CreateAcceptedAsync(command, transaction.Session, token);
                return result is BackupPolicy ? transaction.Commit(result) : transaction.Abort(result);
            },
            ct);

    public Task<UpdateManualBackupPolicyOutcome> UpdateAsync(UpdateBackupPolicyCommand command, CancellationToken ct) =>
        transactions.InvokeOutcomeAsync<UpdateManualBackupPolicyOutcome>(
            async (transaction, token) =>
            {
                if (!await git.AuthorizeAsync(
                        new(command.TenantId, BackupDeclarationKinds.Policy, command.Id),
                        transaction.Session,
                        token))
                    return transaction.Abort(new UpdateManualBackupPolicyOutcome(new BackupPolicyGitOwned()));

                var accepted = await UpdateAcceptedAsync(command, transaction.Session, token);
                UpdateManualBackupPolicyOutcome result = accepted switch
                {
                    BackupPolicy policy => policy,
                    BackupPolicyMissing error => error,
                    BackupPolicyRevisionConflict error => error,
                    BackupPolicyIdentityImmutable error => error,
                    BackupPolicyStopping error => error,
                    BackupPolicyDestinationUnavailable error => error,
                    BackupSourceUnavailable error => error,
                    BackupSourceUnsupported error => error,
                    BackupSourcePlacementUnavailable error => error,
                    BackupSourceRevisionChanged error => error,
                    BackupDestinationPlacementUnavailable error => error,
                    BackupRetentionInvalid error => error,
                };
                return result is BackupPolicy ? transaction.Commit(result) : transaction.Abort(result);
            },
            ct);

    public async Task<CreateBackupPolicyWriteOutcome> CreateAcceptedAsync(
        CreateBackupPolicyCommand command,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var input = command.Input;
        var provider = providers.Find(input.ResourceType);
        if (provider is null)
            return new BackupSourceUnsupported();

        var schedule = Schedule(input);
        if (provider.ValidateSchedule(schedule) is BackupRetentionInvalid invalid)
            return invalid;

        var destination = await FenceDestinationAsync(command.TenantId, input.DestinationId, session, ct);
        if (destination is null)
            return new BackupPolicyDestinationUnavailable();

        var core = EventCore.Create(command.TenantId);
        var origin = new BackupExecutionOrigin(core.TraceId, core.CorrelationId);
        var archiveResult = await createArchive.CreateAsync(
            new(command.TenantId, command.Id, input.ResourceType, input.ResourceId, destination, origin),
            session,
            ct);
        return archiveResult switch
        {
            BackupArchive archive => await PersistAsync(archive),
            BackupSourceUnavailable error => error,
            BackupSourceUnsupported error => error,
            BackupSourcePlacementUnavailable error => error,
            BackupSourceRevisionChanged error => error,
            BackupDestinationPlacementUnavailable error => error,
        };

        async Task<CreateBackupPolicyWriteOutcome> PersistAsync(BackupArchive archive)
        {
            var policy = BackupPolicyFactory.Create(
                command.Id,
                command.TenantId,
                input.Name,
                input.ResourceType,
                input.ResourceId,
                destination.Id,
                archive.Id,
                schedule.CronUtc,
                schedule.Suspended,
                schedule.RetentionDays,
                origin,
                clock.GetUtcNow().UtcDateTime);
            try
            {
                await policies.InsertOneAsync(session, policy, cancellationToken: ct);
            }
            catch (MongoWriteException exception) when (MongoDuplicateKey.IsForIndex(
                                                            exception,
                                                            BackupsIndexes.PolicyName,
                                                            BackupsCollections.Policies))
            {
                return new BackupPolicyNameAlreadyExists();
            }
            catch (MongoWriteException exception) when (MongoDuplicateKey.IsForIndex(
                                                            exception,
                                                            BackupsIndexes.PolicySource,
                                                            BackupsCollections.Policies))
            {
                return new BackupPolicySourceAlreadyBound();
            }

            await outbox(session, BackupPolicyNotifications.Changed(policy), ct);
            return policy;
        }
    }

    public async Task<UpdateAcceptedBackupPolicyOutcome> UpdateAcceptedAsync(
        UpdateBackupPolicyCommand command,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var input = command.Input;
        var current = await policies.Find(session, BackupsFilters<BackupPolicy>.ById(command.TenantId, command.Id))
            .SingleOrDefaultAsync(ct);
        if (current is null)
            return new BackupPolicyMissing();
        if (current.Revision != command.ExpectedRevision)
            return new BackupPolicyRevisionConflict();
        if (current.Deleting)
            return new BackupPolicyStopping();
        if (current.Name != input.Name || current.ResourceType != input.ResourceType ||
            current.ResourceId != input.ResourceId)
            return new BackupPolicyIdentityImmutable();

        var provider = providers.Find(input.ResourceType);
        if (provider is null)
            return new BackupSourceUnsupported();
        var schedule = Schedule(input);
        if (provider.ValidateSchedule(schedule) is BackupRetentionInvalid invalid)
            return invalid;
        if (current.DestinationId == input.DestinationId && current.Schedule == schedule.CronUtc &&
            current.Suspended == schedule.Suspended && current.RetentionDays == schedule.RetentionDays)
            return current;

        var core = EventCore.Create(command.TenantId);
        var origin = new BackupExecutionOrigin(core.TraceId, core.CorrelationId);
        var destination = await FenceDestinationAsync(command.TenantId, input.DestinationId, session, ct);
        if (destination is null)
            return new BackupPolicyDestinationUnavailable();
        if (current.DestinationId == destination.Id)
            return await PersistAsync(current.ArchiveId);

        var archiveResult = await createArchive.CreateAsync(
            new(command.TenantId, command.Id, input.ResourceType, input.ResourceId, destination, origin),
            session,
            ct);
        return archiveResult switch
        {
            BackupArchive archive => await RetireAndPersistAsync(archive),
            BackupSourceUnavailable error => error,
            BackupSourceUnsupported error => error,
            BackupSourcePlacementUnavailable error => error,
            BackupSourceRevisionChanged error => error,
            BackupDestinationPlacementUnavailable error => error,
        };

        async Task<UpdateAcceptedBackupPolicyOutcome> RetireAndPersistAsync(BackupArchive archive)
        {
            await retireArchive.ExecuteAsync(new RetireReplacedBackupArchive(current, archive), session, ct);
            return await PersistAsync(archive.Id);
        }

        async Task<UpdateAcceptedBackupPolicyOutcome> PersistAsync(string archiveId)
        {
            var policy = current.Reconfigure(
                destination.Id,
                archiveId,
                schedule.CronUtc,
                schedule.Suspended,
                schedule.RetentionDays,
                origin,
                clock.GetUtcNow().UtcDateTime);
            var result = await policies.ReplaceOneAsync(
                session,
                BackupConfigurationFilters<BackupPolicy>.AtRevision(
                    command.TenantId,
                    command.Id,
                    command.ExpectedRevision),
                policy,
                cancellationToken: ct);
            if (result.MatchedCount != 1)
                return new BackupPolicyRevisionConflict();

            await outbox(session, BackupPolicyNotifications.Changed(policy), ct);
            return policy;
        }
    }

    private async Task<BackupDestination?> FenceDestinationAsync(
        string tenantId,
        string destinationId,
        IClientSessionHandle session,
        CancellationToken ct) => await destinations.FindOneAndUpdateAsync(
        session,
        BackupsFilters<BackupDestination>.ById(tenantId, destinationId),
        Builders<BackupDestination>.Update.Inc(destination => destination.ReferenceFence, 1),
        cancellationToken: ct);

    private static BackupSchedule Schedule(BackupPolicyInput input) => new(
        UtcCronSchedule.Parse(input.Schedule).Expression,
        input.Suspended,
        input.RetentionDays);
}
