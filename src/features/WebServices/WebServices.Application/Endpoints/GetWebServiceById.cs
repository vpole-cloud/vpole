using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.WebServices.Application.Common;
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
    IWebServiceDbContext dbContext)
    : IMediatorHandler<GetWebServiceByIdRequest, WebServiceDto>
{
    public async Task<Result<WebServiceDto>> HandleAsync(
        GetWebServiceByIdRequest request,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<WebService>.Filter.And(
            Builders<WebService>.Filter.ForTenant(request.TenantId),
            Builders<WebService>.Filter.Eq(ws => ws.Id, request.Id));

        var webService = await dbContext.WebServices
            .Find(filter)
            .FirstOrDefaultAsync(cancellationToken: cancellationToken);

        if (webService is null)
            return Failures.NotFound("Web service not found.", request.Id);

        return webService.ToDto();
    }
}

public record GetWebServiceByIdRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string? TenantId = null
) : IMediatorRequest<WebServiceDto>, ITenantReadOperation;

