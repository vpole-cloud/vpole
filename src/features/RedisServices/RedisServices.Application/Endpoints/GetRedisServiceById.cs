// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitOps.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Declarations;
using Pole.Features.RedisServices.Application.Services;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.RedisServices.Application.Common.RedisHttpMappings;

namespace Pole.Features.RedisServices.Application.Endpoints;

public static class GetRedisServiceById
{
    public const string Route = "/{Id}";

    public static IEndpointRouteBuilder MapGetRedisServiceByIdEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                Route,
                async (
                        IMediator mediator, [AsParameters] GetRedisServiceByIdRequest request,
                        CancellationToken cancellationToken) =>
                    (await mediator.Send(request, cancellationToken)).ToHttpResult(outcome => outcome switch
                    {
                        RedisDto redis => Results.Ok(redis),
                        RedisServiceNotFound notFound => MapNotFound(notFound),
                    }))
            .WithName("GetRedisServiceById")
            .WithDescription("Gets a Redis service by its ID.")
            .Produces<RedisDto>();

        return app;
    }
}

public union GetRedisServiceByIdOutcome(RedisDto, RedisServiceNotFound);

public sealed class GetRedisServiceByIdRequestValidator : AbstractValidator<GetRedisServiceByIdRequest>
{
    public GetRedisServiceByIdRequestValidator() => RuleFor(request => request.Id).MongoObjectId("Redis service ID");
}

public sealed class GetRedisServiceByIdHandler(
    LoadRedisById loadRedisById,
    LoadResourceDeployment loadDeployment,
    IGitOpsMutations git
) : IMediatorOutcomeHandler<GetRedisServiceByIdRequest, GetRedisServiceByIdOutcome>
{
    public async Task<GetRedisServiceByIdOutcome> HandleAsync(
        GetRedisServiceByIdRequest request,
        CancellationToken cancellationToken = default)
    {
        var redis = await loadRedisById(request.TenantId, request.Id, cancellationToken);

        if (redis is null)
            return new RedisServiceNotFound(request.Id);

        var placement = await loadDeployment(
            new LoadResourceDeploymentRequest(
                TenantId: redis.TenantId,
                ResourceType: RedisResourceDeployment.ResourceType,
                ResourceId: redis.Id),
            cancellationToken: cancellationToken);
        var status = await git.StatusAsync(
            new LoadGitResourceStatusRequest(request.TenantId, DeclarativeRedisProvider.ResourceKind, redis.Id),
            cancellationToken);
        return redis.ToDto(placement) with { GitOps = status };
    }
}

public record GetRedisServiceByIdRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<GetRedisServiceByIdOutcome>, ITenantReadOperation;
