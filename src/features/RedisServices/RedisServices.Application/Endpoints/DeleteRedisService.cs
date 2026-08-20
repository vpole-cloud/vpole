using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Events;
using Pole.Features.RedisServices.Domain;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Persistence;
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
    LoadRedisById loadRedisById,
    BeginRedisDeletion beginRedisDeletion,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    RequestResourceDeploymentDeletionLegacy requestDeploymentDeletion,
    LoadResourceDeployment loadDeployment,
    LoadCurrentResourceDeploymentComputeLegacy loadDeploymentCompute,
    TimeProvider timeProvider
    ) : IMediatorHandler<DeleteRedisServiceRequest, DeleteRedisServiceResponse>
{
    public async Task<Result<DeleteRedisServiceResponse>> HandleAsync(DeleteRedisServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var redis = await loadRedisById(
            request.TenantId,
            request.Id,
            cancellationToken);
        if (redis is null)
            return Failures.NotFound("Redis service not found.", request.Id);
        if (redis.Lifecycle is DeletingRedisLifecycle)
        {
            var deletingPlacement = await loadDeployment(
                request.TenantId,
                RedisResourceDeployment.ResourceType,
                request.Id,
                cancellationToken);
            return deletingPlacement is null
                ? Failures.Conflict(
                    "The deleting Redis service has no resource deployment.")
                : new DeleteRedisServiceResponse(request.Id, deletingPlacement);
        }
        
        var currentComputeResult = await loadDeploymentCompute(
            redis.TenantId,
            RedisResourceDeployment.ResourceType,
            redis.Id,
            cancellationToken);
        if (!currentComputeResult.TryGetValue(
                out var currentCompute,
                out var currentComputeFailure))
        {
            return currentComputeFailure;
        }

        var quotaInput = RedisQuotaInput.Create(
            currentCompute.Compute.Requests.CpuMillis,
            currentCompute.Compute.Requests.MemoryMi);
        return await quotaAdmissionExecutor.ExecuteAsync<RedisQuotaInput, DeleteRedisServiceResponse>(
            redis.TenantId,
            RedisQuotaResourceKey.Create(redis.Id),
            new DeleteQuotaMutation<RedisQuotaInput>(quotaInput),
            async (session, transactionCancellationToken) =>
            {
                var deleting = redis.BeginDeletion(
                    timeProvider.GetUtcNow().UtcDateTime);
                var persistenceResult = await beginRedisDeletion(
                    session,
                    redis,
                    deleting,
                    transactionCancellationToken);
                if (persistenceResult is Failure persistenceFailure)
                    return persistenceFailure;

                var deploymentResult = await requestDeploymentDeletion(
                    new ResourceDeploymentMutationRequest(
                        redis.TenantId,
                        RedisResourceDeployment.ResourceType,
                        redis.Id,
                        currentCompute.Compute.Profile),
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
                    deployment);
            },
            cancellationToken);
    }
}

public sealed class FinalizeRedisServiceDeletionConsumer(
    FinalizeRedisDeletion finalizeRedisDeletion)
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

        return finalizeRedisDeletion(
            message.EventCore.TenantId,
            message.ResourceId,
            context.CancellationToken);
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
