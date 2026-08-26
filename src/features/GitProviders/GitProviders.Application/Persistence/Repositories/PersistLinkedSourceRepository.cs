using MongoDB.Driver;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Domain;
using Pole.ResultType;

namespace Pole.Features.GitProviders.Application.Persistence;

public delegate Task<Result<SourceRepository, SourceRepositoryAlreadyLinked>> PersistLinkedSourceRepository(
    SourceRepository repository,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistLinkedSourceRepository(IMongoDatabase database)
{
    public async Task<Result<SourceRepository, SourceRepositoryAlreadyLinked>> InvokeAsync(
        SourceRepository repository,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await database
                .GetCollection<SourceRepository>("SourceRepositories")
                .InsertOneAsync(repository, cancellationToken: cancellationToken);
            return new Ok<SourceRepository>(repository);
        }
        catch (MongoWriteException exception) when (
            exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return new Err<SourceRepositoryAlreadyLinked>(
                new SourceRepositoryAlreadyLinked(
                    repository.ConnectionId,
                    repository.ExternalRepositoryId,
                    repository.Branch,
                    repository.CandidateKey));
        }
    }
}
