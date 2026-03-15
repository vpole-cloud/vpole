using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.Features.Database;
using Pole.Features.RedisServices.Common;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.RedisServices.Endpoints;

public static class GetById
{
    public const string Route = "/{Id}";
    
    public static IEndpointRouteBuilder MapGetRedisServiceByIdEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(Route, async (IMediator mediator, [AsParameters] GetRedisServiceByIdRequest request, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetRedisServiceById")
            .WithDescription("Gets a Redis service by its ID.")
            .Produces<RedisDto>();
        
        return app;
    }
}

public sealed class GetRedisServiceByIdHandler(
    MongoDbContext dbContext)
    : IMediatorHandler<GetRedisServiceByIdRequest, RedisDto>
{
    public async Task<Result<RedisDto>> HandleAsync(
        GetRedisServiceByIdRequest request,
        CancellationToken cancellationToken = default)
    {
        var redis = await dbContext.RedisServices
            .Find(r => r.Id == request.Id)
            .FirstOrDefaultAsync(cancellationToken: cancellationToken);

        if (redis is null)
            return Result.NotFound($"Redis service with ID '{request.Id}' not found.");

        return redis.ToDto();
    }
}

public record GetRedisServiceByIdRequest(
    [FromRoute] string Id
) : IMediatorRequest<RedisDto>;
