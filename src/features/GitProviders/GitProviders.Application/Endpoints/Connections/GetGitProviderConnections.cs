using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.Persistence.Connections;
using Pole.Mediator;

namespace Pole.Features.GitProviders.Application.Endpoints.Connections;

public sealed record GetGitProviderConnectionsRequest(
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<GetGitProviderConnectionsOutcome>, ITenantReadOperation;

public union GetGitProviderConnectionsOutcome(IReadOnlyList<GitProviderConnectionDto>);

public sealed class GetGitProviderConnectionsHandler(
    LoadGitProviderConnections loadGitProviderConnections)
    : IMediatorOutcomeHandler<GetGitProviderConnectionsRequest, GetGitProviderConnectionsOutcome>
{
    public async Task<GetGitProviderConnectionsOutcome> HandleAsync(
        GetGitProviderConnectionsRequest request,
        CancellationToken cancellationToken = default)
    {
        var connections = await loadGitProviderConnections(request.TenantId, cancellationToken);
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
                return result.ToHttpResult(outcome => outcome switch
                {
                    IReadOnlyList<GitProviderConnectionDto> connections => Results.Ok(connections)
                });
            })
            .WithName("GetGitProviderConnections")
            .Produces<IReadOnlyList<GitProviderConnectionDto>>();
        return app;
    }
}
