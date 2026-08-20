using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Persistence;
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
    LoadRedisById loadRedisById,
    LoadResourceDeployment loadDeployment)
    : IMediatorHandler<GetRedisServiceByIdRequest, RedisDto>
{
    public async Task<Result<RedisDto>> HandleAsync(
        GetRedisServiceByIdRequest request,
        CancellationToken cancellationToken = default)
    {
        var redis = await loadRedisById(
            request.TenantId,
            request.Id,
            cancellationToken);

        if (redis is null)
            return Failures.NotFound("Redis service not found.", request.Id);

        var placement = await loadDeployment(
            redis.TenantId,
            RedisResourceDeployment.ResourceType,
            redis.Id,
            cancellationToken);
        return redis.ToDto(placement);
    }
}

public record GetRedisServiceByIdRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId
) : IMediatorRequest<RedisDto>, ITenantReadOperation;
