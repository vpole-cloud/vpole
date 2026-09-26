using MongoDB.Driver;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.Persistence.Repositories;

public union PersistLinkedSourceRepositoryOutcome(
    SourceRepository,
    SourceRepositoryAlreadyLinked);

public delegate Task<PersistLinkedSourceRepositoryOutcome> PersistLinkedSourceRepository(
    SourceRepository repository,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistLinkedSourceRepository(IMongoDatabase database)
{
    public async Task<PersistLinkedSourceRepositoryOutcome> InvokeAsync(
        SourceRepository repository,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await database
                .GetCollection<SourceRepository>("SourceRepositories")
                .InsertOneAsync(repository, cancellationToken: cancellationToken);
            return repository;
        }
        catch (MongoWriteException exception) when (
            exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return new SourceRepositoryAlreadyLinked(
                    repository.ConnectionId,
                    repository.ExternalRepositoryId,
                    repository.Branch,
                    repository.CandidateKey);
        }
    }
}
