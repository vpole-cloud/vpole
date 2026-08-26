using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Application.Persistence.Reads;
using Pole.Features.WebServices.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Endpoints;

public static class GetWebServiceById
{
    public const string Route = "/{Id}";
    
    public static IEndpointRouteBuilder MapGetWebServiceByIdEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(Route, async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] GetWebServiceByIdRequest request) =>
            {
                var result = await mediator.Send(request);
                return result.ToHttpResult(outcome => outcome switch
                {
                    WebServiceDto value => Results.Ok(value),
                    WebServiceNotFound error => httpProblems.Map(error).ToHttpResult()
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
    LoadResourceDeploymentSet loadResourceDeploymentSet)
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
            webService.TenantId,
            WebServiceResourceDeployment.ResourceType,
            webService.Id,
            cancellationToken);
        return webService.ToDto(deployments);
    }
}

public record GetWebServiceByIdRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId
) : IMediatorRequest<GetWebServiceByIdOutcome>, ITenantReadOperation;
