using MongoDB.Driver;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;
using Pole.ResultType;

namespace Pole.Features.SourceBuilds.Application.Common;

public sealed class SourceBuildReferenceLookup(ISourceBuildDbContext dbContext)
    : ISourceBuildReferenceLookup
{
    public async Task<Result<bool>> HasConfigurationForSourceRepositoryAsync(
        string tenantId,
        string sourceRepositoryId,
        CancellationToken cancellationToken = default) =>
        await dbContext.SourceBuildConfigurations
            .Find(item =>
                item.TenantId == tenantId &&
                item.SourceRepositoryId == sourceRepositoryId)
            .AnyAsync(cancellationToken);

    public async Task<Result<bool>> HasConfigurationForWebServiceAsync(
        string tenantId,
        string webServiceId,
        CancellationToken cancellationToken = default) =>
        await dbContext.SourceBuildConfigurations
            .Find(item =>
                item.TenantId == tenantId &&
                item.WebServiceId == webServiceId)
            .AnyAsync(cancellationToken);
}
