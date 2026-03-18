using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.WebServices.Common;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.WebServices.Endpoints;

public static class GetById
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
    MongoDbContext dbContext)
    : IMediatorHandler<GetWebServiceByIdRequest, WebServiceDto>
{
    public async Task<Result<WebServiceDto>> HandleAsync(
        GetWebServiceByIdRequest request,
        CancellationToken cancellationToken = default)
    {
        var webService = await dbContext.WebServices
            .Find(ws => ws.Id == request.Id)
            .FirstOrDefaultAsync(cancellationToken: cancellationToken);

        if (webService is null)
            return Result.NotFound($"Web service with ID '{request.Id}' not found.");

        return webService.ToDto();
    }
}

public record GetWebServiceByIdRequest(
    [FromRoute] string Id
) : IMediatorRequest<WebServiceDto>;