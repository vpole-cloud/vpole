using Pole.Architecture;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Application.Mongo;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Runs;

internal sealed class GitDatabaseClock(IMongoCollection<RepositorySource> sources)
{
    [ViolateArchitecture(ArchitectureRules.RawBson,
        "Read $$NOW from MongoDB within the caller transaction; the source match must not use a client clock.")]
    public async Task<DateTime> NowAsync(GitSourceIdentity identity, IClientSessionHandle session, CancellationToken ct)
    {
        var document = await sources.Aggregate(session)
            .Match(GitRepositoryFilters.Source(identity.TenantId, identity.SourceId))
            .Project<GitDatabaseTime>(new BsonDocument(
                MongoFields.Field<GitDatabaseTime, DateTime>(time => time.Now), "$$NOW")).FirstAsync(ct);
        return document.Now;
    }
}

[BsonIgnoreExtraElements]
internal sealed record GitDatabaseTime([property: BsonDateTimeOptions(Kind = DateTimeKind.Utc)] DateTime Now);
