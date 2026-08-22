using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using FluentValidation;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Persistence;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.RedisServices.Application.Endpoints;

public static class GetRedisServiceById
{
    public const string Route = "/{Id}";
    
    public static IEndpointRouteBuilder MapGetRedisServiceByIdEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(Route, async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] GetRedisServiceByIdRequest request,
                CancellationToken cancellationToken) =>
                (await mediator.Send(request, cancellationToken))
                .ToHttpResult(outcome => outcome switch
                {
                    RedisDto redis => Results.Ok(redis),
                    RedisServiceNotFound notFound => httpProblems.Map(notFound).ToHttpResult()
                }))
            .WithName("GetRedisServiceById")
            .WithDescription("Gets a Redis service by its ID.")
            .Produces<RedisDto>();
        
        return app;
    }
}

public union GetRedisServiceByIdOutcome(
    RedisDto,
    RedisServiceNotFound);

public sealed class GetRedisServiceByIdRequestValidator
    : AbstractValidator<GetRedisServiceByIdRequest>
{
    public GetRedisServiceByIdRequestValidator() =>
        RuleFor(request => request.Id).MongoObjectId("Redis service ID");
}

public sealed class GetRedisServiceByIdHandler(
    LoadRedisById loadRedisById,
    LoadResourceDeployment loadDeployment)
    : IMediatorOutcomeHandler<GetRedisServiceByIdRequest, GetRedisServiceByIdOutcome>
{
    public async Task<GetRedisServiceByIdOutcome> HandleAsync(
        GetRedisServiceByIdRequest request,
        CancellationToken cancellationToken = default)
    {
        var redis = await loadRedisById(
            request.TenantId,
            request.Id,
            cancellationToken);

        if (redis is null)
            return new RedisServiceNotFound(request.Id);

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
) : IMediatorRequest<GetRedisServiceByIdOutcome>, ITenantReadOperation;
