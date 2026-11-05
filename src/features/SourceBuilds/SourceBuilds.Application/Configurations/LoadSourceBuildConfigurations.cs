using MongoDB.Driver;
using Pole.Features.SourceBuilds.Application.Mongo;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Configurations;

public delegate Task<IReadOnlyList<SourceBuildConfiguration>> LoadSourceBuildConfigurations(
    string tenantId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadSourceBuildConfigurations(
    IMongoCollection<SourceBuildConfiguration> configurations)
{
    public async Task<IReadOnlyList<SourceBuildConfiguration>> InvokeAsync(
        string tenantId,
        CancellationToken cancellationToken = default) =>
        await configurations
            .Find(SourceBuildConfigurationFilters.VisibleToTenant(tenantId))
            .SortBy(item => item.CreatedAtUtc)
            .ToListAsync(cancellationToken);
}
