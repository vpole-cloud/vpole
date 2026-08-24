using MongoDB.Driver;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;
using Pole.ResultType;

namespace Pole.Features.SourceBuilds.Application.Common;

public sealed class SourceBuildReferenceLookup(ISourceBuildDbContext dbContext)
{
    public async Task<Result<Unit, SourceBuildWebServiceConfigurationExists>>
        EnsureNoWebServiceConfigurationAsync(
            string tenantId,
            string webServiceId,
        CancellationToken cancellationToken = default)
    {
        var hasConfiguration = await dbContext.SourceBuildConfigurations
            .Find(SourceBuildFilters.ConfigurationsByTenantAndWebService(tenantId, webServiceId))
            .AnyAsync(cancellationToken);
        return hasConfiguration
            ? new Err<SourceBuildWebServiceConfigurationExists>(
                new SourceBuildWebServiceConfigurationExists(webServiceId))
            : new Ok<Unit>(Unit.Value);
    }

    public async Task<Result<Unit, SourceBuildSourceRepositoryConfigurationExists>>
        EnsureNoSourceRepositoryConfigurationAsync(
        string tenantId,
        string sourceRepositoryId,
        CancellationToken cancellationToken = default)
    {
        var hasConfiguration = await dbContext.SourceBuildConfigurations
            .Find(SourceBuildFilters.ConfigurationsByTenantAndSourceRepository(
                tenantId,
                sourceRepositoryId))
            .AnyAsync(cancellationToken);
        return hasConfiguration
            ? new Err<SourceBuildSourceRepositoryConfigurationExists>(
                new SourceBuildSourceRepositoryConfigurationExists(sourceRepositoryId))
            : new Ok<Unit>(Unit.Value);
    }

}
