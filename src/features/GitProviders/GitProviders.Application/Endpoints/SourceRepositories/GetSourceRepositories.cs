using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
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

public static class GetSourceRepositoriesEndpoint
{
    public static IEndpointRouteBuilder MapGetSourceRepositoriesEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (IMediator mediator, [AsParameters] GetSourceRepositoriesRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetSourceRepositories")
            .Produces<IReadOnlyList<SourceRepositoryDto>>();
        return app;
    }
}
