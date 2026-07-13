using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.GitProviders.Application.Common;
using Pole.Mediator;

namespace Pole.Features.GitProviders.Application.Endpoints;

public static class GitProviderConnections
{
    public static IEndpointRouteBuilder MapGitProviderConnectionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (IMediator mediator, [AsParameters] GetGitProviderConnectionsRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetGitProviderConnections")
            .Produces<IReadOnlyList<GitProviderConnectionDto>>();

        app.MapPost("/github/install", async (IMediator mediator, [AsParameters] BeginGitHubInstallationRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("BeginGitHubInstallation")
            .Produces<BeginGitHubInstallationResponse>();

        app.MapDelete("/{ConnectionId}", async (IMediator mediator, [AsParameters] DeleteGitProviderConnectionRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(_ => Results.NoContent());
            })
            .WithName("DeleteGitProviderConnection")
            .Produces(StatusCodes.Status204NoContent);

        return app;
    }
}
