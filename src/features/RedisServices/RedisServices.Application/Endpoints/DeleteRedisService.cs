using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Mutations;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceDeployments.Events;
using Pole.Features.RedisServices.Domain;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Events;
using Pole.Features.Quotas.Contracts;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.RedisServices.Application.Endpoints;

public static class DeleteRedisService
{
    public static IEndpointRouteBuilder MapDeleteRedisServiceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/{Id}", async (IMediator mediator, [AsParameters] DeleteRedisServiceRequest request, CancellationToken ct) =>
            {
                var result = await mediator.Send(request, ct);
                return result.ToHttpResult(x => Results.Accepted(value: x));
            })
            .WithName("DeleteRedisService")
            .Produces(StatusCodes.Status202Accepted);

        return app;
    }
}

public sealed class DeleteRedisServiceHandler(
    IRedisDbContext dbContext,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    IResourceDeploymentMutationWriter deploymentWriter,
    IResourceDeploymentQueryService deploymentQuery,
    TimeProvider timeProvider
    ) : IMediatorHandler<DeleteRedisServiceRequest, DeleteRedisServiceResponse>
{
    public async Task<Result<DeleteRedisServiceResponse>> HandleAsync(DeleteRedisServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<Redis>.Filter.And(
            Builders<Redis>.Filter.ForTenant(request.TenantId),
            Builders<Redis>.Filter.Eq(r => r.Id, request.Id));

        var redis = await dbContext.RedisServices
            .Find(filter)
            .FirstOrDefaultAsync(cancellationToken);
        if (redis is null)
            return Failures.NotFound("Redis service not found.", request.Id);
        if (redis.Lifecycle is DeletingRedisLifecycle)
        {
            var currentPlacement = await deploymentQuery.GetAsync(
                request.TenantId,
                RedisResourceDeployment.ResourceType,
                request.Id,
                cancellationToken);
            return currentPlacement is null
                ? Failures.Conflict(
                    "The deleting Redis service has no resource deployment.")
                : new DeleteRedisServiceResponse(request.Id, currentPlacement);
        }
        
        return await quotaAdmissionExecutor.ExecuteAsync<RedisSpec, DeleteRedisServiceResponse>(
            redis.TenantId,
            RedisQuotaResourceKey.Create(redis.Id),
            new DeleteQuotaMutation<RedisSpec>(redis.Spec),
            async (session, transactionCancellationToken) =>
            {
                var deleting = redis.BeginDeletion(
                    timeProvider.GetUtcNow().UtcDateTime);
                var replace = await dbContext.RedisServices.ReplaceOneAsync(
                    session,
                    Builders<Redis>.Filter.And(
                        filter,
                        Builders<Redis>.Filter.Eq(
                            item => item.Revision,
                            redis.Revision)),
                    deleting,
                    cancellationToken: transactionCancellationToken);
                if (replace.ModifiedCount != 1)
                {
                    return Failures.Conflict(
                        "The Redis service changed while deletion was being accepted.");
                }

                var deploymentResult = await deploymentWriter.RequestDeletionAsync(
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

                return new DeleteRedisServiceResponse(
                    request.Id,
                    deployment.ToPlacementSummaryDto());
            },
            cancellationToken);
    }
}

public sealed class FinalizeRedisServiceDeletionConsumer(
    IRedisDbContext dbContext,
    IResourceDeploymentDbContext deploymentDbContext)
    : IConsumer<ResourceDeploymentDeletedEvent>
{
    public Task Consume(ConsumeContext<ResourceDeploymentDeletedEvent> context)
    {
        var message = context.Message;
        if (!string.Equals(
                message.ResourceType,
                RedisResourceDeployment.ResourceType.Value,
                StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        return dbContext.RunInTransactionAsync(async (session, ct) =>
        {
            var redisFilter = Builders<Redis>.Filter.And(
                Builders<Redis>.Filter.ForTenant(message.EventCore.TenantId),
                Builders<Redis>.Filter.Eq(
                    redis => redis.Id,
                    message.ResourceId),
                Builders<Redis>.Filter.OfType<
                    RedisLifecycle,
                    DeletingRedisLifecycle>(
                    redis => redis.Lifecycle));
            var redis = await dbContext.RedisServices
                .Find(session, redisFilter)
                .FirstOrDefaultAsync(ct);
            if (redis is null)
                return Result.Success();

            var deployments = await deploymentDbContext.ResourceDeployments
                .Find(session, deployment =>
                    deployment.TenantId == message.EventCore.TenantId &&
                    deployment.ResourceType ==
                        RedisResourceDeployment.ResourceType.Value &&
                    deployment.ResourceId == message.ResourceId)
                .ToListAsync(ct);
            if (deployments.Count == 0 ||
                deployments.Any(deployment =>
                    deployment.State is not DeletedResourceDeploymentState))
            {
                return Result.Success();
            }

            var deleted = await dbContext.RedisServices.DeleteOneAsync(
                session,
                filter: redisFilter,
                cancellationToken: ct);
            if (deleted.DeletedCount != 1)
                return Result.Success();

            await deploymentDbContext.ResourceDeployments.DeleteManyAsync(
                session,
                filter: deployment =>
                    deployment.TenantId == message.EventCore.TenantId &&
                    deployment.ResourceType ==
                        RedisResourceDeployment.ResourceType.Value &&
                    deployment.ResourceId == message.ResourceId,
                cancellationToken: ct);
            await dbContext.StoreEventAsync(
                redis.ToDeleteEvent(),
                session,
                ct);
            return Result.Success();
        }, cancellationToken: context.CancellationToken);
    }
}

public sealed record DeleteRedisServiceRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId
) : IMediatorRequest<DeleteRedisServiceResponse>, ITenantWriteOperation;

public sealed record DeleteRedisServiceResponse(
    string Id,
    PlacementSummaryDto Placement
);
