using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox.Contracts;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.Persistence.Repositories;

public union PersistLinkedSourceRepositoryOutcome(
    SourceRepository,
    SourceRepositoryAlreadyLinked);

public delegate Task<PersistLinkedSourceRepositoryOutcome> PersistLinkedSourceRepository(
    SourceRepository repository,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistLinkedSourceRepository(
    IMongoDatabase database,
    MongoRunTransaction transactions,
    StoreOutboxWrites outbox)
{
    public async Task<PersistLinkedSourceRepositoryOutcome> InvokeAsync(
        SourceRepository repository,
        CancellationToken cancellationToken = default)
    {
        return await transactions.InvokeOutcomeAsync<PersistLinkedSourceRepositoryOutcome>(async (tx, ct) =>
            {
                try
                {
                    await database
                        .GetCollection<SourceRepository>("SourceRepositories")
                        .InsertOneAsync(tx.Session, repository, cancellationToken: ct);
                }
                catch (MongoWriteException exception) when (
                    exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
                {
                    return tx.Abort(new SourceRepositoryAlreadyLinked(
                        repository.ConnectionId,
                        repository.ExternalRepositoryId,
                        repository.Branch,
                        repository.CandidateKey));
                }

                await outbox(tx.Session, [OutboxWrite.Create(repository.ToChangedEvent())], ct);
                return tx.Commit(repository);
            },
            cancellationToken);
    }
}
