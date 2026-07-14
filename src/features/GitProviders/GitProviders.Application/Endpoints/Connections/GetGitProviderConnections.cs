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

public sealed record GetGitProviderConnectionsRequest(
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<IReadOnlyList<GitProviderConnectionDto>>, ITenantReadOperation;

public sealed class GetGitProviderConnectionsHandler(IGitProviderDbContext dbContext)
    : IMediatorHandler<GetGitProviderConnectionsRequest, IReadOnlyList<GitProviderConnectionDto>>
{
    public async Task<Result<IReadOnlyList<GitProviderConnectionDto>>> HandleAsync(
        GetGitProviderConnectionsRequest request,
        CancellationToken cancellationToken = default)
    {
        var connections = await dbContext.GitProviderConnections
            .Find(Builders<GitProviderConnection>.Filter.ForTenant(request.TenantId))
            .SortBy(item => item.Provider)
            .ThenBy(item => item.AccountLogin)
            .ToListAsync(cancellationToken);
        return connections.Select(item => item.ToDto()).ToArray();
    }
}

public static class GetGitProviderConnectionsEndpoint
{
    public static IEndpointRouteBuilder MapGetGitProviderConnectionsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (IMediator mediator, [AsParameters] GetGitProviderConnectionsRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetGitProviderConnections")
            .Produces<IReadOnlyList<GitProviderConnectionDto>>();
        return app;
    }
}
