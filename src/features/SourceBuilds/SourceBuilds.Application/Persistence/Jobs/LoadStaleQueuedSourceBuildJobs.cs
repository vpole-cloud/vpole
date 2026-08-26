using MongoDB.Driver;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Persistence;

public delegate Task<IReadOnlyList<string>> LoadStaleQueuedSourceBuildJobs(
    DateTime cutoffUtc,
    int limit,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadStaleQueuedSourceBuildJobs(IMongoCollection<SourceBuildJob> jobs)
{
    public async Task<IReadOnlyList<string>> InvokeAsync(
        DateTime cutoffUtc,
        int limit,
        CancellationToken cancellationToken = default) =>
        await jobs
            .Find(SourceBuildJobFilters.StaleQueued(cutoffUtc))
            .SortBy(item => item.UpdatedAtUtc)
            .Limit(limit)
            .Project(item => item.Id)
            .ToListAsync(cancellationToken);
}
