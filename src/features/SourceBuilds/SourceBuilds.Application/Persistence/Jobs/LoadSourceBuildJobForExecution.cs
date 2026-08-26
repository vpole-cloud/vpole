using MongoDB.Driver;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Persistence.Jobs;

public delegate Task<SourceBuildJob?> LoadSourceBuildJobForExecution(
    string buildJobId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadSourceBuildJobForExecution(IMongoCollection<SourceBuildJob> jobs)
{
    public async Task<SourceBuildJob?> InvokeAsync(
        string buildJobId,
        CancellationToken cancellationToken = default) =>
        await jobs.Find(SourceBuildJobFilters.ById(buildJobId))
            .FirstOrDefaultAsync(cancellationToken);
}
