// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Tokens;
using Pole.Features.WebServices.Contracts;
using Pole.Mediator;
using static Pole.Features.WebServices.Application.Common.WebServiceHttpMappings;

namespace Pole.Features.WebServices.Application.Endpoints.DeploymentTokens;

public sealed record GetWebServiceDeploymentTokensRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<GetWebServiceDeploymentTokensOutcome>, ITenantReadOperation;

public union GetWebServiceDeploymentTokensOutcome(IReadOnlyList<WebServiceDeploymentTokenDto>, WebServiceNotFound);

public sealed class GetWebServiceDeploymentTokensHandler(
    WebServiceExists webServiceExists,
    LoadWebServiceDeploymentTokens loadTokens
) : IMediatorOutcomeHandler<GetWebServiceDeploymentTokensRequest, GetWebServiceDeploymentTokensOutcome>
{
    public async Task<GetWebServiceDeploymentTokensOutcome> HandleAsync(
        GetWebServiceDeploymentTokensRequest request,
        CancellationToken cancellationToken = default)
    {
        var exists = await webServiceExists(request.TenantId, request.Id, cancellationToken);
        if (!exists)
            return new WebServiceNotFound(request.Id);

        var tokens = await loadTokens(request.TenantId, request.Id, cancellationToken);

        return new GetWebServiceDeploymentTokensOutcome(tokens.Select(token => token.ToDto()).ToArray());
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
        app.MapGet(
                "/{Id}/deployment-tokens",
                async (
                    IMediator mediator, [AsParameters] GetWebServiceDeploymentTokensRequest request,
                    CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(request, cancellationToken);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        IReadOnlyList<WebServiceDeploymentTokenDto> value => Results.Ok(value),
                        WebServiceNotFound error => MapError(error),
                    });
                })
            .WithName("GetWebServiceDeploymentTokens")
            .Produces<IReadOnlyList<WebServiceDeploymentTokenDto>>();
        return app;
    }
}
