using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.Mongo;
using Pole.Features.GitProviders.Domain;
using Pole.Features.Outbox.Contracts;

namespace Pole.Features.GitProviders.Application.Repositories;

public union PersistLinkedSourceRepositoryOutcome(
    SourceRepository,
    SourceRepositoryAlreadyLinked,
    SourceRepositoryNameConflict,
    GitProviderConnectionNotFound);

public delegate Task<PersistLinkedSourceRepositoryOutcome> PersistLinkedSourceRepository(
    SourceRepository repository,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistLinkedSourceRepository(
    IMongoCollection<SourceRepository> repositories,
    IMongoCollection<GitProviderConnection> connections,
    MongoRunTransaction transactions,
    StoreOutboxWrites outbox
)
{
    public Task<PersistLinkedSourceRepositoryOutcome> InvokeAsync(
        SourceRepository repository,
        CancellationToken cancellationToken = default) =>
        transactions.InvokeOutcomeAsync<PersistLinkedSourceRepositoryOutcome>(
            async (tx, ct) =>
            {
                var result = await CreateAsync(repository, tx.Session, ct);
                return result is SourceRepository ? tx.Commit(result) : tx.Abort(result);
            },
            cancellationToken);

    public async Task<PersistLinkedSourceRepositoryOutcome> CreateAsync(
        SourceRepository repository,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var fenced = await connections.UpdateOneAsync(
            session,
            GitProviderFilters.ConnectionByTenantAndId(repository.TenantId, repository.ConnectionId),
            Builders<GitProviderConnection>.Update.Inc(item => item.ReferenceFence, 1),
            cancellationToken: cancellationToken);
        if (fenced.MatchedCount == 0)
            return new GitProviderConnectionNotFound(repository.ConnectionId);

        try
        {
            await repositories.InsertOneAsync(session, repository, cancellationToken: cancellationToken);
        }
        catch (MongoWriteException exception) when (MongoDuplicateKey.IsForIndex(
                                                        exception,
                                                        SourceRepositoryIndexes.TechnicalName,
                                                        repositories.CollectionNamespace.CollectionName))
        {
            return new SourceRepositoryNameConflict(repository.TechnicalName);
        }
        catch (MongoWriteException exception) when (MongoDuplicateKey.IsForIndex(
                                                        exception,
                                                        SourceRepositoryIndexes.Location,
                                                        repositories.CollectionNamespace.CollectionName))
        {
            return new SourceRepositoryAlreadyLinked(
                repository.ConnectionId,
                repository.ExternalRepositoryId,
                repository.Branch);
        }

        await outbox(session, [OutboxWrite.Create(repository.ToChangedEvent())], cancellationToken);
        return repository;
    }
}
