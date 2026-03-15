using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using Pole.Features.Database;
using Pole.Features.RedisServices.Common;
using Pole.Features.Request;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.RedisServices.Endpoints;

public static class Get
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapGetRedisServicesEndpoint()
        {
            app.MapGet("/", async (IMediator mediator, [AsParameters] GetRedisServicesRequest request) =>
            {
                var result = await mediator.Send(request);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetRedisServices")
            .WithSummary("Retrieves all Redis services.")
            .WithDescription("Fetches a list of all Redis services available in the system.")
            .Produces<List<RedisDto>>();

            return app;
        }
    }
}

public sealed class GetRedisServicesHandler(
    MongoDbContext dbContext)
    : IMediatorHandler<GetRedisServicesRequest, PaginatedResponse<RedisDto>>
{
    public async Task<Result<PaginatedResponse<RedisDto>>> HandleAsync(
        GetRedisServicesRequest request,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.RedisServices.AsQueryable();
        if (!string.IsNullOrWhiteSpace(request.Search))
            query = query.Where(rs => rs.Name.Contains(request.Search, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(request.ResourceGroupId))
            query = query.Where(rs => rs.ResourceGroupId == request.ResourceGroupId);

        var totalCount = await query.CountAsync(cancellationToken: cancellationToken);

        var redisServices = await query
            .ApplyPagination(request)
            .ToListAsync(cancellationToken: cancellationToken);

        return new PaginatedResponse<RedisDto>(
            Items: redisServices.Select(x => x.ToDto()),
            TotalCount: totalCount,
            PageNumber: request.PageNumber,
            PageSize: request.PageSize);
    }
}

public record GetRedisServicesRequest : PaginatedRequest<RedisDto>
{
    [FromQuery] public string? Search { get; init; }
    [FromQuery] public string? ResourceGroupId { get; init; }
}
