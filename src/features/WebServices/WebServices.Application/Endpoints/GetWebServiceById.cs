using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Endpoints;

public static class GetWebServiceById
{
    public const string Route = "/{Id}";
    
    public static IEndpointRouteBuilder MapGetWebServiceByIdEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(Route, async (IMediator mediator, [AsParameters] GetWebServiceByIdRequest request) =>
            {
                var result = await mediator.Send(request);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetWebServiceById")
            .WithDescription("Gets a web service by its ID.")
            .Produces<WebServiceDto>(StatusCodes.Status200OK);
        
        return app;
    }
}

public sealed class GetWebServiceByIdHandler(
    LoadWebServiceById loadWebServiceById,
    IResourceDeploymentSetQueryService deploymentQuery)
    : IMediatorHandler<GetWebServiceByIdRequest, WebServiceDto>
{
    public async Task<Result<WebServiceDto>> HandleAsync(
        GetWebServiceByIdRequest request,
        CancellationToken cancellationToken = default)
    {
        var webService = await loadWebServiceById(
            request.TenantId,
            request.Id,
            cancellationToken);

        if (webService is null)
            return Failures.NotFound("Web service not found.", request.Id);

        var deployments = await deploymentQuery.GetAllAsync(
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
) : IMediatorRequest<WebServiceDto>, ITenantReadOperation;
