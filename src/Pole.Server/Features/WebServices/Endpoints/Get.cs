using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Pole.Server.Database;
using Pole.Server.Features.Common.Abstractions;
using Pole.Server.Features.WebServices.Common;
using Pole.Server.Mediator;
using Pole.Server.ResultType;

namespace Pole.Server.Features.WebServices.Endpoints;

public static class Get
{

    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapGetWebServicesEndpoint()
        {
            app.MapGet("/", async (IMediator mediator, [AsParameters] GetWebServicesRequest request) =>
            {
                var result = await mediator.Send(request);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetWebServices")
            .WithSummary("Retrieves all web services.")
            .WithDescription("Fetches a list of all web services available in the system.")
            .Produces<List<WebServiceDto>>();

            return app;
        }
    }
}

public sealed class GetWebServicesHandler(
    MongoDbContext dbContext)
    : IMediatorHandler<GetWebServicesRequest, PaginatedResponse<WebServiceDto>>
{
    public async Task<Result<PaginatedResponse<WebServiceDto>>> HandleAsync(
        GetWebServicesRequest request,
        CancellationToken cancellationToken = default)
    {
        var filter = request.ResourceGroupId is not null
            ? Builders<WebService>.Filter.Eq(ws => ws.ResourceGroupId, request.ResourceGroupId)
            : Builders<WebService>.Filter.Empty;

        var totalCount = (int)await dbContext.WebServices
            .CountDocumentsAsync(filter, cancellationToken: cancellationToken);

        var webServices = await dbContext.WebServices
            .Find(filter)
            .ApplyPagination(request)
            .ToListAsync(cancellationToken: cancellationToken);

        return new PaginatedResponse<WebServiceDto>(
            Items: webServices.Select(x => x.ToDto()),
            TotalCount: totalCount,
            PageNumber: request.PageNumber,
            PageSize: request.PageSize);
    }
}

public record GetWebServicesRequest(
    [FromQuery] string? ResourceGroupId = null)
    : PaginatedRequest<WebServiceDto>;