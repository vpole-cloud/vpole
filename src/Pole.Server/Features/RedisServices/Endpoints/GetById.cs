using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Pole.Server.Database;
using Pole.Server.Features.RedisServices.Common;
using Pole.Server.Mediator;
using Pole.Server.ResultType;

namespace Pole.Server.Features.RedisServices.Endpoints;

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
