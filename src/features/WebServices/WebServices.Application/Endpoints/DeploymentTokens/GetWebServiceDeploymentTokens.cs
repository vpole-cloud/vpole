using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Endpoints;

public sealed record GetWebServiceDeploymentTokensRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId
) : IMediatorRequest<IReadOnlyList<WebServiceDeploymentTokenDto>>, ITenantReadOperation;

public sealed class GetWebServiceDeploymentTokensHandler(
    WebServiceExists webServiceExists,
    LoadWebServiceDeploymentTokens loadTokens)
    : IMediatorHandler<GetWebServiceDeploymentTokensRequest, IReadOnlyList<WebServiceDeploymentTokenDto>>
{
    public async Task<Result<IReadOnlyList<WebServiceDeploymentTokenDto>>> HandleAsync(
        GetWebServiceDeploymentTokensRequest request,
        CancellationToken cancellationToken = default)
    {
        var exists = await webServiceExists(
            request.TenantId,
            request.Id,
            cancellationToken);
        if (!exists)
            return Failures.NotFound("Web service not found.", request.Id);

        var tokens = await loadTokens(
            request.TenantId,
            request.Id,
            cancellationToken);

        return tokens.Select(token => token.ToDto()).ToArray();
    }
}

public sealed class GetWebServiceDeploymentTokensValidator : AbstractValidator<GetWebServiceDeploymentTokensRequest>
{
    public GetWebServiceDeploymentTokensValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.TenantId).NotEmpty();
    }
}

public static class GetWebServiceDeploymentTokensEndpoint
{
    public static IEndpointRouteBuilder MapGetWebServiceDeploymentTokensEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{Id}/deployment-tokens", async (IMediator mediator,
                [AsParameters] GetWebServiceDeploymentTokensRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetWebServiceDeploymentTokens")
            .Produces<IReadOnlyList<WebServiceDeploymentTokenDto>>();
        return app;
    }
}
