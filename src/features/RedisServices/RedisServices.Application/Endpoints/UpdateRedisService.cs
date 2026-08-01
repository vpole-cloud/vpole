using System.Collections.Immutable;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.Quotas.Contracts;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Application.Mutations;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Domain;
using Pole.Features.RedisServices.Events;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.RedisServices.Application.Endpoints;

public static class UpdateRedisService
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapUpdateRedisServiceEndpoint()
        {
            app.MapPut("/{Id}", async (IMediator mediator, [AsParameters] UpdateRedisServiceRequest request, CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(request, cancellationToken);
                    return result.ToHttpResult(x => Results.Accepted(value: x));
                })
                .WithName("UpdateRedisService")
                .Produces<UpdateRedisServiceResponse>(StatusCodes.Status202Accepted);

            return app;
        }
    }
}

public sealed record UpdateRedisServiceRequest(
    [FromRoute] string Id,
    [FromBody] UpdateRedisServiceRequestBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId
) : IMediatorRequest<UpdateRedisServiceResponse>, ITenantWriteOperation;

public sealed record UpdateRedisServiceRequestBody(
    RedisSpecDto Spec,
    ImmutableHashSet<string> Tags
);

public sealed record UpdateRedisServiceResponse(
    string Id,
    PlacementSummaryDto Placement);

public sealed class UpdateRedisServiceHandler(
    IRedisDbContext dbContext,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    IResourceDeploymentMutationWriter deploymentWriter)
    : IMediatorHandler<UpdateRedisServiceRequest, UpdateRedisServiceResponse>
{
    public async Task<Result<UpdateRedisServiceResponse>> HandleAsync(
        UpdateRedisServiceRequest request,
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
        if (redis.Lifecycle is not ActiveRedisLifecycle)
            return Failures.Conflict("A deleting Redis service cannot be updated.");

        if (!string.Equals(
                request.Body.Spec.Storage.StorageProfileId,
                redis.Spec.Storage.StorageProfileId,
                StringComparison.Ordinal))
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["Body.Spec.Storage.StorageProfileId"] = ["Storage profile cannot be changed for an existing Redis service."]
            });
        }

        var nextSpec = request.Body.Spec.ToDomain();
        return await quotaAdmissionExecutor.ExecuteAsync<RedisSpec, UpdateRedisServiceResponse>(
            redis.TenantId,
            RedisQuotaResourceKey.Create(redis.Id),
            new UpdateQuotaMutation<RedisSpec>(redis.Spec, nextSpec),
            async (session, transactionCancellationToken) =>
            {
                var updatedRedis = redis.UpdateDesiredState(
                    nextSpec,
                    request.Body.Tags);
                var changes = GetChanges(redis, updatedRedis);
                var replace = await dbContext.RedisServices.ReplaceOneAsync(
                    session,
                    Builders<Redis>.Filter.And(
                        filter,
                        Builders<Redis>.Filter.Eq(
                            item => item.Revision,
                            redis.Revision)),
                    updatedRedis,
                    cancellationToken: transactionCancellationToken);
                if (replace.ModifiedCount != 1)
                {
                    return Failures.Conflict(
                        "The Redis service changed while the update was being accepted.");
                }

                var deploymentResult = await deploymentWriter.RequestApplyAsync(
                    new ResourceDeploymentMutationCommand(
                        redis.TenantId,
                        RedisResourceDeployment.ResourceType,
                        redis.Id),
                    session,
                    transactionCancellationToken);
                if (!deploymentResult.TryGetValue(
                        out var deployment,
                        out var deploymentFailure))
                {
                    return deploymentFailure;
                }

                await dbContext.StoreEventAsync(
                    new RedisServiceUpdatedEvent(
                        request.Id,
                        redis.Name,
                        redis.ResourceGroupId,
                        changes)
                    {
                        EventCore = EventCore.Create(redis.TenantId)
                    },
                    session,
                    transactionCancellationToken);

                return new UpdateRedisServiceResponse(
                    redis.Id,
                    deployment.ToPlacementSummaryDto());
            },
            cancellationToken);
    }

    private static RedisServiceUpdatedEventChanges GetChanges(
        Redis redis,
        Redis updatedRedis) =>
        new(UpdateLog<RedisServiceSpecSnapshot>.Of(
                redis.Spec.ToSnapshot(),
                updatedRedis.Spec.ToSnapshot()),
            UpdateLog<ImmutableHashSet<string>>.Of(
                redis.Tags,
                updatedRedis.Tags));
}

public sealed class UpdateRedisServiceValidator : AbstractValidator<UpdateRedisServiceRequest>
{
    public UpdateRedisServiceValidator()
    {
        RuleFor(x => x.Id)
            .NotEmpty().WithMessage("Redis service ID is required.");

        RuleFor(x => x.Body.Spec)
            .NotNull().WithMessage("Redis service specification is required.")
            .SetValidator(new RedisSpecDtoValidator());
    }
}
