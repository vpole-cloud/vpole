using MongoDB.Driver;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;
using Pole.ResultType;

namespace Pole.Features.SourceBuilds.Application.Persistence.Configuration;

internal sealed class MongoEnsureNoWebServiceActiveSourceBuilds(
    IMongoCollection<SourceBuildJob> jobs)
{
    public async Task<Result<Unit, SourceBuildWebServiceHasActiveJobs>> InvokeAsync(
        string tenantId,
        string webServiceId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var hasActiveJobs = await jobs
            .Find(session, SourceBuildJobFilters.ActiveForWebService(tenantId, webServiceId))
            .AnyAsync(cancellationToken);
        return hasActiveJobs
            ? new Err<SourceBuildWebServiceHasActiveJobs>(
                new SourceBuildWebServiceHasActiveJobs(webServiceId))
            : new Ok<Unit>(Unit.Value);
    }
}
