using MongoDB.Driver;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Persistence.Configuration;

internal sealed class MongoEnsureNoWebServiceActiveSourceBuilds(
    IMongoCollection<SourceBuildJob> jobs)
{
    public async Task<EnsureNoWebServiceActiveSourceBuildsOutcome> InvokeAsync(
        string tenantId,
        string webServiceId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var hasActiveJobs = await jobs
            .Find(session, SourceBuildJobFilters.ActiveForWebService(tenantId, webServiceId))
            .AnyAsync(cancellationToken);
        return hasActiveJobs
            ? new SourceBuildWebServiceHasActiveJobs(webServiceId)
            : new NoActiveSourceBuilds();
    }
}
