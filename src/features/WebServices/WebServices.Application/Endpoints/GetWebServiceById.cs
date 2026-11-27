// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Queries;
using Pole.Mediator;
using static Pole.Features.WebServices.Application.Common.WebServiceHttpMappings;

namespace Pole.Features.WebServices.Application.Endpoints;

public static class GetWebServiceById
{
    public const string Route = "/{Id}";

    public static IEndpointRouteBuilder MapGetWebServiceByIdEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                Route,
                async (IMediator mediator, [AsParameters] GetWebServiceByIdRequest request) =>
                {
                    var result = await mediator.Send(request);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        WebServiceDto value => Results.Ok(value),
                        WebServiceNotFound error => MapError(error),
                    });
                })
            .WithName("GetWebServiceById")
            .WithDescription("Gets a web service by its ID.")
            .Produces<WebServiceDto>();

        return app;
    }
}

public union GetWebServiceByIdOutcome(WebServiceDto, WebServiceNotFound);

public sealed class GetWebServiceByIdHandler(
    LoadWebServiceById loadWebServiceById,
    LoadResourceDeploymentSet loadResourceDeploymentSet,
    IGitOpsMutations git
) : IMediatorOutcomeHandler<GetWebServiceByIdRequest, GetWebServiceByIdOutcome>
{
    public async Task<GetWebServiceByIdOutcome> HandleAsync(
        GetWebServiceByIdRequest request,
        CancellationToken cancellationToken = default)
    {
        var webService = await loadWebServiceById(request.TenantId, request.Id, cancellationToken);

        if (webService is null)
            return new WebServiceNotFound(request.Id);

        var deployments = await loadResourceDeploymentSet(
            new LoadResourceDeploymentSetRequest(
                TenantId: webService.TenantId,
                ResourceType: WebServiceResourceDeployment.ResourceType,
                ResourceId: webService.Id),
            cancellationToken: cancellationToken);
        return webService.ToDto(deployments) with
        {
            GitOps = await git.StatusAsync(
                new LoadGitResourceStatusRequest(
                    TenantId: webService.TenantId,
                    Kind: "WebService",
                    ResourceId: webService.Id),
                ct: cancellationToken),
        };
    }
}

public record GetWebServiceByIdRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<GetWebServiceByIdOutcome>, ITenantReadOperation;
