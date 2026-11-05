using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Runs;

internal sealed class GitDatabaseClock(IMongoCollection<RepositorySource> sources)
{
    public async Task<DateTime> NowAsync(GitSourceIdentity identity, IClientSessionHandle session, CancellationToken ct)
    {
        var document = await sources.Aggregate(session)
            .Match(GitRepositoryFilters.Source(identity.TenantId, identity.SourceId))
            .Project(new BsonDocument("Now", "$$NOW")).FirstAsync(ct);
        return document["Now"].AsBsonDateTime.ToUniversalTime();
    }
}
