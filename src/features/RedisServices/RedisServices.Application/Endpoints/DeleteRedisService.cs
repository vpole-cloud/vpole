using MassTransit;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Events;
using Pole.Features.RedisServices.Domain;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Persistence;
using Pole.Features.Quotas.Contracts;
using Pole.Features.RedisServices.Application.Persistence.Lifecycle;
using Pole.Features.RedisServices.Application.Persistence.Reads;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;
using static Pole.Features.RedisServices.Application.Common.RedisHttpMappings;
using static Pole.Features.ResourceDeployments.Contracts.HttpMapping.ResourceDeploymentContractHttpMappings;
using static Pole.Features.Quotas.Contracts.HttpMapping.QuotaContractHttpMappings;

namespace Pole.Features.RedisServices.Application.Endpoints;

public static class DeleteRedisService
{
    public static IEndpointRouteBuilder MapDeleteRedisServiceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/{Id}", async (
                IMediator mediator,
                [AsParameters] DeleteRedisServiceRequest request,
                CancellationToken ct) =>
                (await mediator.Send(request, ct))
                .ToHttpResult(outcome => outcome switch
                {
                    DeleteRedisServiceResponse response => Results.Accepted(value: response),
                    RedisServiceNotFound error => MapNotFound(error),
                    RedisDeletionResourceDeploymentMissing error => MapDeletionResourceDeploymentMissing(error),
                    RedisDeletionConflict error => MapDeletionConflict(error),
                    LoadCurrentResourceDeploymentComputeError error => MapComputeError(error),
                    ResourceDeploymentMutationError error => MapMutationError(error),
                    QuotaExecutionError error => MapExecutionError(error)
                }))
            .WithName("DeleteRedisService")
            .Produces(StatusCodes.Status202Accepted);

        return app;
    }
}

public sealed record RedisDeletionResourceDeploymentMissing(string RedisId);

public union DeleteRedisServiceOutcome(
    DeleteRedisServiceResponse,
    RedisServiceNotFound,
    RedisDeletionResourceDeploymentMissing,
    LoadCurrentResourceDeploymentComputeError,
    RedisDeletionConflict,
    ResourceDeploymentMutationError,
    QuotaExecutionError);

public sealed class DeleteRedisServiceHandler(
    LoadRedisById loadRedisById,
    BeginRedisDeletion beginRedisDeletion,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    RequestResourceDeploymentDeletion requestDeploymentDeletion,
    LoadResourceDeployment loadDeployment,
    LoadCurrentResourceDeploymentCompute loadDeploymentCompute,
    TimeProvider timeProvider
    ) : IMediatorOutcomeHandler<DeleteRedisServiceRequest, DeleteRedisServiceOutcome>
{
    public async Task<DeleteRedisServiceOutcome> HandleAsync(DeleteRedisServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var redis = await loadRedisById(
            request.TenantId,
            request.Id,
            cancellationToken);
        if (redis is null)
        {
            return new RedisServiceNotFound(request.Id);
        }
        if (redis.Lifecycle is DeletingRedisLifecycle)
        {
            var deletingPlacement = await loadDeployment(
                request.TenantId,
                RedisResourceDeployment.ResourceType,
                request.Id,
                cancellationToken);
            return deletingPlacement is null
                ? new RedisDeletionResourceDeploymentMissing(redis.Id)
                : new DeleteRedisServiceOutcome(
                    new DeleteRedisServiceResponse(request.Id, deletingPlacement));
        }

        var currentComputeResult = await loadDeploymentCompute(
            redis.TenantId,
            RedisResourceDeployment.ResourceType,
            redis.Id,
            cancellationToken);
        if (!currentComputeResult.TryGetValue(
                out var currentCompute,
                out var currentComputeError))
        {
            return currentComputeError;
        }

        var quotaInput = RedisQuotaInput.Create(
            currentCompute.Compute.Requests.CpuMillis,
            currentCompute.Compute.Requests.MemoryMi);
        return await quotaAdmissionExecutor.ExecuteOutcomeAsync<
            RedisQuotaInput,
            DeleteRedisServiceOutcome>(
            redis.TenantId,
            RedisQuotaResourceKey.Create(redis.Id),
            new DeleteQuotaMutation<RedisQuotaInput>(quotaInput),
            quotaError => quotaError,
            async (transaction, transactionCancellationToken) =>
            {
                var deleting = redis.BeginDeletion(
                    timeProvider.GetUtcNow().UtcDateTime);
                var persistenceResult = await beginRedisDeletion(
                    transaction.Session,
                    redis,
                    deleting,
                    transactionCancellationToken);
                if (persistenceResult is RedisDeletionConflict persistenceError)
                {
                    return transaction.Abort(persistenceError);
                }

                var deploymentResult = await requestDeploymentDeletion(
                    new ResourceDeploymentMutationRequest(
                        redis.TenantId,
                        RedisResourceDeployment.ResourceType,
                        redis.Id,
                        currentCompute.Compute.Profile),
                    transaction.Session,
                    transactionCancellationToken);
                if (!deploymentResult.TryGetValue(
                        out var deployment,
                        out var deploymentError))
                {
                    return transaction.Abort(deploymentError);
                }

                return transaction.Commit(new DeleteRedisServiceOutcome(
                    new DeleteRedisServiceResponse(request.Id, deployment)));
            },
            cancellationToken);
    }
}

public sealed class FinalizeRedisServiceDeletionConsumer(
    FinalizeRedisDeletion finalizeRedisDeletion)
    : IConsumer<ResourceDeploymentDeletedEvent>
{
    public async Task Consume(ConsumeContext<ResourceDeploymentDeletedEvent> context)
    {
        var message = context.Message;
        if (!string.Equals(
                message.ResourceType,
                RedisResourceDeployment.ResourceType.Value,
                StringComparison.Ordinal))
        {
            return;
        }

        var result = await finalizeRedisDeletion(
            message.EventCore.TenantId,
            message.ResourceId,
            context.CancellationToken);
        if (result is RedisDeletionFinalizationConflict conflict)
        {
            throw new InvalidOperationException(
                $"Redis deletion finalization conflicted for '{conflict.RedisId}'.");
        }
    }
}

public sealed record DeleteRedisServiceRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<DeleteRedisServiceOutcome>, ITenantWriteOperation;

public sealed record DeleteRedisServiceResponse(
    string Id,
    PlacementSummaryDto Placement
);

public sealed class DeleteRedisServiceRequestValidator
    : AbstractValidator<DeleteRedisServiceRequest>
{
    public DeleteRedisServiceRequestValidator() =>
        RuleFor(request => request.Id).MongoObjectId("Redis service ID");
}
