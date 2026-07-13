using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.SourceBuilds.Application.Endpoints;

public sealed record GetSourceBuildConfigurationsRequest(
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<IReadOnlyList<SourceBuildConfigurationDto>>, ITenantReadOperation;

public sealed class GetSourceBuildConfigurationsHandler(ISourceBuildDbContext dbContext)
    : IMediatorHandler<GetSourceBuildConfigurationsRequest, IReadOnlyList<SourceBuildConfigurationDto>>
{
    public async Task<Result<IReadOnlyList<SourceBuildConfigurationDto>>> HandleAsync(
        GetSourceBuildConfigurationsRequest request,
        CancellationToken cancellationToken = default)
    {
        var configurations = await dbContext.SourceBuildConfigurations
            .Find(item => item.TenantId == request.TenantId)
            .SortBy(item => item.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        return configurations.Select(item => item.ToDto()).ToArray();
    }
}
