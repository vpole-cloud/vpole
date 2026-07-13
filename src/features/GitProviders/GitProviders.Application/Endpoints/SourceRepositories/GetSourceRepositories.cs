using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.GitProviders.Application.Endpoints;

public sealed record GetSourceRepositoriesRequest(
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<IReadOnlyList<SourceRepositoryDto>>, ITenantReadOperation;

public sealed class GetSourceRepositoriesHandler(IGitProviderDbContext dbContext)
    : IMediatorHandler<GetSourceRepositoriesRequest, IReadOnlyList<SourceRepositoryDto>>
{
    public async Task<Result<IReadOnlyList<SourceRepositoryDto>>> HandleAsync(
        GetSourceRepositoriesRequest request,
        CancellationToken cancellationToken = default)
    {
        var repositories = await dbContext.SourceRepositories
            .Find(Builders<SourceRepository>.Filter.ForTenant(request.TenantId))
            .SortBy(item => item.RepositoryFullName)
            .ThenBy(item => item.Branch)
            .ToListAsync(cancellationToken);
        return repositories.Select(item => item.ToDto()).ToArray();
    }
}
