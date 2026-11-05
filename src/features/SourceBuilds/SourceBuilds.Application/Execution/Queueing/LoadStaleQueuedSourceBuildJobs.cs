using MongoDB.Driver;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Execution.Queueing;

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
            .Find(SourceBuildJobFilters.RecoverableBefore(cutoffUtc))
            .SortBy(item => item.UpdatedAtUtc)
            .Limit(limit)
            .Project(item => item.Id)
            .ToListAsync(cancellationToken);
}
