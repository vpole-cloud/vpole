using MongoDB.Driver;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;
using Pole.ResultType;

namespace Pole.Features.SourceBuilds.Application.Persistence;

internal sealed class MongoEnsureNoWebServiceSourceBuildConfiguration(
    IMongoCollection<SourceBuildConfiguration> configurations)
{
    public async Task<Result<Unit, SourceBuildWebServiceConfigurationExists>> InvokeAsync(
        string tenantId,
        string webServiceId,
        CancellationToken cancellationToken = default)
    {
        var hasConfiguration = await configurations
            .Find(SourceBuildConfigurationFilters.ByWebService(tenantId, webServiceId))
            .AnyAsync(cancellationToken);
        return hasConfiguration
            ? new Err<SourceBuildWebServiceConfigurationExists>(
                new SourceBuildWebServiceConfigurationExists(webServiceId))
            : new Ok<Unit>(Unit.Value);
    }
}
