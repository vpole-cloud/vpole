using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.WebServices.Application.Common;
using Pole.Mediator;

namespace Pole.Features.WebServices.Application.Endpoints;

public static class WebServiceDeploymentTokens
{
    public static IEndpointRouteBuilder MapWebServiceDeploymentTokenEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{Id}/deployment-tokens", async (IMediator mediator, [AsParameters] GetWebServiceDeploymentTokensRequest request, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetWebServiceDeploymentTokens")
            .Produces<IReadOnlyList<WebServiceDeploymentTokenDto>>();

        app.MapPost("/{Id}/deployment-tokens", async (IMediator mediator, [AsParameters] CreateWebServiceDeploymentTokenRequest request, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(response => Results.Created(
                    $"/api/v1/web-services/{request.Id}/deployment-tokens/{response.Token.Id}",
                    response));
            })
            .WithName("CreateWebServiceDeploymentToken")
            .Produces<CreateWebServiceDeploymentTokenResponse>(StatusCodes.Status201Created);

        app.MapDelete("/{Id}/deployment-tokens/{TokenId}", async (IMediator mediator, [AsParameters] RevokeWebServiceDeploymentTokenRequest request, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(_ => Results.NoContent());
            })
            .WithName("RevokeWebServiceDeploymentToken")
            .Produces(StatusCodes.Status204NoContent);

        return app;
    }
}
