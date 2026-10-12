using MongoDB.Driver;
using Pole.Database;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Persistence.Jobs;

public delegate Task<SourceBuildJob?> ClaimQueuedSourceBuildJob(
    string buildJobId,
    DateTime claimedAtUtc,
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
        string buildJobId,
        DateTime claimedAtUtc,
        CancellationToken cancellationToken = default)
    {
        // The internal dispatcher supplies job IDs; the claimed document owns tenant context.
        var outcome = await transactions.InvokeOutcomeAsync<ClaimSourceBuildJobOutcome>(async (transaction, ct) =>
        {
            var claimed = await jobs.FindOneAndUpdateAsync(
                transaction.Session,
                SourceBuildJobFilters.ClaimQueued(buildJobId),
                Builders<SourceBuildJob>.Update
                    .Set(item => item.State, new CloningSourceBuild(claimedAtUtc))
                    .Set(item => item.UpdatedAtUtc, claimedAtUtc),
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
        }, cancellationToken);
        return outcome switch
        {
            SourceBuildJob claimed => claimed,
            SourceBuildJobNotClaimed => null
        };
    }
}
