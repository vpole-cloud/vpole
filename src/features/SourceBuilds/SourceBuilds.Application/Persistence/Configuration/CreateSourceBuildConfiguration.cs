using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.Outbox.Contracts;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Persistence.Configuration;

public sealed record CreateSourceBuildConfigurationCommand(
    SourceBuildConfiguration Configuration,
    long RepositoryRevision);

public union PersistCreatedSourceBuildConfigurationOutcome(
    SourceBuildConfiguration,
    SourceBuildConfigurationAlreadyExists,
    SourceBuildConfigurationWebServiceNotFound,
    SourceBuildConfigurationNameConflict,
    SourceBuildConfigurationRepositoryChanged);

public delegate Task<PersistCreatedSourceBuildConfigurationOutcome> CreateSourceBuildConfiguration(
    CreateSourceBuildConfigurationCommand command,
    CancellationToken cancellationToken = default);

internal sealed class MongoCreateSourceBuildConfiguration(
    IMongoCollection<SourceBuildConfiguration> configurations,
    MongoRunTransaction transactions,
    FenceSourceRepository fenceRepository,
    AdmitSourceBuildWebService admitWebService,
    StoreOutboxWrites outbox)
{
    public Task<PersistCreatedSourceBuildConfigurationOutcome> InvokeAsync(
        CreateSourceBuildConfigurationCommand command,
        CancellationToken cancellationToken = default) =>
        transactions.InvokeOutcomeAsync<PersistCreatedSourceBuildConfigurationOutcome>(async (transaction, ct) =>
        {
            var result = await InsertAsync(command, transaction.Session, ct);
            return result is SourceBuildConfiguration ? transaction.Commit(result) : transaction.Abort(result);
        }, cancellationToken);

    public async Task<PersistCreatedSourceBuildConfigurationOutcome> InsertAsync(
        CreateSourceBuildConfigurationCommand command,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var configuration = command.Configuration;
        if (await fenceRepository(new(configuration.TenantId, configuration.SourceRepositoryId,
            command.RepositoryRevision), session, cancellationToken) is not SourceRepositoryFenced)
            return new SourceBuildConfigurationRepositoryChanged(configuration.SourceRepositoryId);

        if (await configurations.Find(session,
            SourceBuildConfigurationFilters.ByName(configuration.TenantId, configuration.TechnicalName))
            .AnyAsync(cancellationToken))
            return new SourceBuildConfigurationNameConflict(configuration.TechnicalName);

        if (await configurations.Find(session,
            SourceBuildConfigurationFilters.ByWebService(configuration.TenantId, configuration.WebServiceId))
            .AnyAsync(cancellationToken))
            return new SourceBuildConfigurationAlreadyExists(configuration.WebServiceId);

        if (await admitWebService(new(configuration.TenantId, configuration.WebServiceId, configuration.Id,
            SourceBuildAdmissionPurpose.Configure),
            session, cancellationToken) is SourceBuildWebServiceNotFound)
            return new SourceBuildConfigurationWebServiceNotFound(configuration.WebServiceId);

        try
        {
            await configurations.InsertOneAsync(session, configuration, cancellationToken: cancellationToken);
        }
        catch (MongoWriteException exception) when (MongoDuplicateKey.IsForIndex(exception,
            SourceBuildIndexes.TenantName, configurations.CollectionNamespace.CollectionName))
        {
            return new SourceBuildConfigurationNameConflict(configuration.TechnicalName);
        }
        catch (MongoWriteException exception) when (MongoDuplicateKey.IsForIndex(exception,
            SourceBuildIndexes.TenantWebService, configurations.CollectionNamespace.CollectionName))
        {
            return new SourceBuildConfigurationAlreadyExists(configuration.WebServiceId);
        }

        await outbox(session, [OutboxWrite.Create(configuration.ToChangedEvent())], cancellationToken);
        return configuration;
    }
}
