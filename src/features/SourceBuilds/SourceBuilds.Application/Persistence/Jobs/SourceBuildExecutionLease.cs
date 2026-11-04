using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Persistence.Jobs;

public sealed record SourceBuildExecutionIdentity(
    string TenantId,
    string BuildJobId,
    string WorkerId,
    long Attempt);

public delegate Task<bool> RenewSourceBuildExecution(
    SourceBuildExecutionIdentity identity,
    CancellationToken cancellationToken = default);

internal static class SourceBuildExecutionLease
{
    internal const int LeaseMilliseconds = 120_000;

    internal static BsonValue Expiry =>
        new BsonDocument("$add", new BsonArray { "$$NOW", LeaseMilliseconds });
}

internal sealed class MongoRenewSourceBuildExecution(IMongoCollection<SourceBuildJob> jobs)
{
    public async Task<bool> InvokeAsync(SourceBuildExecutionIdentity identity,
        CancellationToken cancellationToken = default)
    {
        var update = new BsonDocument("$set",
            new BsonDocument("Execution.ExpiresAtUtc", SourceBuildExecutionLease.Expiry));
        var result = await jobs.UpdateOneAsync(SourceBuildJobFilters.OwnedLease(identity),
            new PipelineUpdateDefinition<SourceBuildJob>(new[] { update }), cancellationToken: cancellationToken);
        return result.MatchedCount == 1;
    }
}
