using MongoDB.Driver;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Persistence.Configuration;

internal sealed class MongoCleanupSourceBuildConfigurations(
    IMongoCollection<SourceBuildConfiguration> configurations)
{
    public async Task InvokeAsync(
        string tenantId,
        string webServiceId,
        CancellationToken cancellationToken = default)
    {
        await configurations.DeleteManyAsync(
            SourceBuildConfigurationFilters.ByWebService(tenantId, webServiceId),
            cancellationToken);
    }
}
