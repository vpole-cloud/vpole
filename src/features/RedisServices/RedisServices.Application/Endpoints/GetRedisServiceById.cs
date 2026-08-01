using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.RedisServices.Domain;
using Pole.Features.RedisServices.Application.Common;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.RedisServices.Application.Endpoints;

public static class GetRedisServiceById
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
    IRedisDbContext dbContext,
    IResourceDeploymentQueryService deploymentQuery)
    : IMediatorHandler<GetRedisServiceByIdRequest, RedisDto>
{
    public async Task<Result<RedisDto>> HandleAsync(
        GetRedisServiceByIdRequest request,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<Redis>.Filter.And(
            Builders<Redis>.Filter.ForTenant(request.TenantId),
            Builders<Redis>.Filter.Eq(r => r.Id, request.Id));

        var redis = await dbContext.RedisServices
            .Find(filter)
            .FirstOrDefaultAsync(cancellationToken: cancellationToken);

        if (redis is null)
            return Failures.NotFound("Redis service not found.", request.Id);

        var placement = await deploymentQuery.GetAsync(
            redis.TenantId,
            ResourceDeploymentResourceTypes.Redis,
            redis.Id,
            cancellationToken);
        return redis.ToDto(placement);
    }
}

public record GetRedisServiceByIdRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId
) : IMediatorRequest<RedisDto>, ITenantReadOperation;
