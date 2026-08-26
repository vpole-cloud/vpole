using MongoDB.Driver;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Persistence.Jobs;

public delegate Task<SourceBuildJob?> ClaimQueuedSourceBuildJob(
    string buildJobId,
    DateTime claimedAtUtc,
    CancellationToken cancellationToken = default);

internal sealed class MongoClaimQueuedSourceBuildJob(IMongoCollection<SourceBuildJob> jobs)
{
    public async Task<SourceBuildJob?> InvokeAsync(
        string buildJobId,
        DateTime claimedAtUtc,
        CancellationToken cancellationToken = default) =>
        await jobs.FindOneAndUpdateAsync(
            SourceBuildJobFilters.ClaimQueued(buildJobId),
            Builders<SourceBuildJob>.Update
                .Set(item => item.State, new CloningSourceBuild(claimedAtUtc))
                .Set(item => item.UpdatedAtUtc, claimedAtUtc),
            new FindOneAndUpdateOptions<SourceBuildJob>
            {
                ReturnDocument = ReturnDocument.After
            },
            cancellationToken);
}
