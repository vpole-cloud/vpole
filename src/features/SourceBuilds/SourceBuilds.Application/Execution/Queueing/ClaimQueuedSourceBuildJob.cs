using Pole.Features.SourceBuilds.Application.Mongo;
using MongoDB.Driver;
using MongoDB.Bson;
using Pole.Database;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Execution.Queueing;

public sealed record ClaimSourceBuildJobRequest(
    string BuildJobId,
    string WorkerId);

public delegate Task<SourceBuildJob?> ClaimQueuedSourceBuildJob(
    ClaimSourceBuildJobRequest request,
    CancellationToken cancellationToken = default);

internal sealed record SourceBuildJobNotClaimed;

internal union ClaimSourceBuildJobOutcome(
    SourceBuildJob,
    SourceBuildJobNotClaimed);

internal sealed class MongoClaimQueuedSourceBuildJob(
    IMongoCollection<SourceBuildJob> jobs,
    MongoRunTransaction transactions,
    StoreOutboxWrites storeOutboxWrites)
{
    public async Task<SourceBuildJob?> InvokeAsync(
        ClaimSourceBuildJobRequest request,
        CancellationToken cancellationToken = default)
    {
        // The internal dispatcher supplies job IDs; the claimed document owns tenant context.
        var outcome = await transactions.InvokeOutcomeAsync<ClaimSourceBuildJobOutcome>(async (transaction, ct) =>
            {
                var claimed = await jobs.FindOneAndUpdateAsync(
                    transaction.Session,
                    SourceBuildJobFilters.ById(request.BuildJobId) & SourceBuildJobFilters.Recoverable,
                    new PipelineUpdateDefinition<SourceBuildJob>(new[]
                    {
                        new BsonDocument("$unset", "State"),
                        new BsonDocument("$set", new BsonDocument
                        {
                            ["Execution"] = new BsonDocument
                            {
                                ["_t"] = new BsonArray
                                {
                                    nameof(SourceBuildExecution), nameof(ClaimedSourceBuildExecution)
                                },
                                ["WorkerId"] = new BsonDocument("$literal", request.WorkerId),
                                ["Attempt"] = new BsonDocument("$add", new BsonArray { "$Execution.Attempt", 1L }),
                                ["ExpiresAtUtc"] = SourceBuildExecutionLease.Expiry
                            },
                            ["State"] = new BsonDocument
                            {
                                ["_t"] = new BsonArray { nameof(SourceBuildJobState), nameof(CloningSourceBuild) },
                                ["StartedAtUtc"] = "$$NOW"
                            },
                            ["UpdatedAtUtc"] = "$$NOW"
                        })
                    }),
                    new FindOneAndUpdateOptions<SourceBuildJob> { ReturnDocument = ReturnDocument.After },
                    ct);
                if (claimed is null)
                {
                    ClaimSourceBuildJobOutcome rejected = new SourceBuildJobNotClaimed();
                    return transaction.Commit(rejected);
                }

                await storeOutboxWrites(transaction.Session, [OutboxWrite.Create(claimed.ToStateChangedEvent())], ct);
                ClaimSourceBuildJobOutcome accepted = claimed;
                return transaction.Commit(accepted);
            },
            cancellationToken);
        return outcome switch
        {
            SourceBuildJob claimed => claimed,
            SourceBuildJobNotClaimed => null
        };
    }
}
