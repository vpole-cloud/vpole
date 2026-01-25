using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
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
        var query = dbContext.WebServices.AsQueryable();
        
        if (request.ResourceGroupId is not null)
            query = query.Where(ws => ws.ResourceGroupId == request.ResourceGroupId);
        if (!string.IsNullOrEmpty(request.Search))
            query = query.Where(ws => ws.Name.Contains(request.Search) || ws.Id.Contains(request.Search));
        
        var totalCount = await query.CountAsync(cancellationToken);
        var webServices = await query
            .ApplyPagination(request)
            .ToListAsync(cancellationToken);

        return new PaginatedResponse<WebServiceDto>(
            Items: webServices.Select(x => x.ToDto()),
            TotalCount: totalCount,
            PageNumber: request.PageNumber,
            PageSize: request.PageSize);
    }
}

public record GetWebServicesRequest : PaginatedRequest<WebServiceDto>
{
    [FromQuery] public string? ResourceGroupId { get; init; }
    [FromQuery] public string? Search { get; init; }
}