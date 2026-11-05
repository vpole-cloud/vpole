using Pole.Features.GitOps.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application;
using Pole.Features.WebServices.Application.Configuration;
using Pole.Features.WebServices.Application.Declarations;
using Pole.Features.WebServices.Application.Hostnames;
using Pole.Features.WebServices.Application.Lifecycle;
using Pole.Features.WebServices.Application.Mongo;
using Pole.Features.WebServices.Application.Connections;
using Pole.Features.WebServices.Application.Queries;
using Pole.Features.WebServices.Domain;
using Pole.Mediator;
using Pole.ResultType;

using static Pole.Features.WebServices.Application.Common.WebServiceHttpMappings;

namespace Pole.Features.WebServices.Application.Endpoints;

public static class GetWebServiceById
{
    public const string Route = "/{Id}";

    public static IEndpointRouteBuilder MapGetWebServiceByIdEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(Route, async (
                IMediator mediator,
                [AsParameters] GetWebServiceByIdRequest request) =>
            {
                var result = await mediator.Send(request);
                return result.ToHttpResult(outcome => outcome switch
                {
                    WebServiceDto value => Results.Ok(value),
                    WebServiceNotFound error => MapError(error)
                });
            })
            .WithName("GetWebServiceById")
            .WithDescription("Gets a web service by its ID.")
            .Produces<WebServiceDto>(StatusCodes.Status200OK);

        return app;
    }
}

public union GetWebServiceByIdOutcome(
    WebServiceDto,
    WebServiceNotFound);

public sealed class GetWebServiceByIdHandler(
    LoadWebServiceById loadWebServiceById,
    LoadResourceDeploymentSet loadResourceDeploymentSet,
    IGitOpsMutations git)
    : IMediatorOutcomeHandler<GetWebServiceByIdRequest, GetWebServiceByIdOutcome>
{
    public async Task<GetWebServiceByIdOutcome> HandleAsync(
        GetWebServiceByIdRequest request,
        CancellationToken cancellationToken = default)
    {
        var webService = await loadWebServiceById(
            request.TenantId,
            request.Id,
            cancellationToken);

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
                ct: cancellationToken)
        };
    }
}

public record GetWebServiceByIdRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId) : IMediatorRequest<GetWebServiceByIdOutcome>, ITenantReadOperation;
