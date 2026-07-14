using MongoDB.Driver;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;
using Pole.ResultType;

namespace Pole.Features.SourceBuilds.Application.Common;

public sealed class SourceBuildReferenceLookup(ISourceBuildDbContext dbContext)
    : ISourceBuildReferenceLookup
{
    public async Task<Result> EnsureSourceRepositoryCanBeDeletedAsync(
        string tenantId,
        string sourceRepositoryId,
        CancellationToken cancellationToken = default)
    {
        var hasConfiguration = await dbContext.SourceBuildConfigurations
            .Find(item =>
                item.TenantId == tenantId &&
                item.SourceRepositoryId == sourceRepositoryId)
            .AnyAsync(cancellationToken);
        return hasConfiguration
            ? Failures.Conflict("Delete the source build configuration before unlinking this repository.")
            : Result.Success();
    }

    public async Task<Result> EnsureWebServiceCanBeDeletedAsync(
        string tenantId,
        string webServiceId,
        CancellationToken cancellationToken = default)
    {
        var hasConfiguration = await dbContext.SourceBuildConfigurations
            .Find(item =>
                item.TenantId == tenantId &&
                item.WebServiceId == webServiceId)
            .AnyAsync(cancellationToken);
        return hasConfiguration
            ? Failures.Conflict("Delete the source build configuration before deleting this web service.")
            : Result.Success();
    }
}
