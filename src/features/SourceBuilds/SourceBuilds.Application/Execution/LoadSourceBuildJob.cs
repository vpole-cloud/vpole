using MongoDB.Driver;
using Pole.Features.SourceBuilds.Application.Mongo;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Execution;

public delegate Task<SourceBuildJob?> LoadSourceBuildJob(
    string tenantId,
    string buildJobId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadSourceBuildJob(
    IMongoCollection<SourceBuildJob> jobs
)
{
    public async Task<SourceBuildJob?> InvokeAsync(
        string tenantId,
        string buildJobId,
        CancellationToken cancellationToken = default) => await jobs
        .Find(SourceBuildJobFilters.ById(tenantId, buildJobId))
        .FirstOrDefaultAsync(cancellationToken);
}
