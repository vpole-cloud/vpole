using MongoDB.Driver;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Common;

public sealed class SourceBuildReferenceLookup(ISourceBuildDbContext dbContext)
    : ISourceBuildReferenceLookup
{
    public Task<bool> HasConfigurationForSourceRepositoryAsync(
        string tenantId,
        string sourceRepositoryId,
        CancellationToken cancellationToken = default) =>
        dbContext.SourceBuildConfigurations
            .Find(item =>
                item.TenantId == tenantId &&
                item.SourceRepositoryId == sourceRepositoryId)
            .AnyAsync(cancellationToken);

    public Task<bool> HasConfigurationForWebServiceAsync(
        string tenantId,
        string webServiceId,
        CancellationToken cancellationToken = default) =>
        dbContext.SourceBuildConfigurations
            .Find(item =>
                item.TenantId == tenantId &&
                item.WebServiceId == webServiceId)
            .AnyAsync(cancellationToken);
}
