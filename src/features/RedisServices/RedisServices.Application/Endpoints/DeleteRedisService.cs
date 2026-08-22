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
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

namespace Pole.Features.RedisServices.Application.Endpoints;

public static class DeleteRedisService
{
    public static IEndpointRouteBuilder MapDeleteRedisServiceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/{Id}", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] DeleteRedisServiceRequest request,
                CancellationToken ct) =>
                (await mediator.Send(request, ct))
                .ToHttpResult(outcome => outcome switch
                {
                    DeleteRedisServiceResponse response => Results.Accepted(value: response),
                    DeleteRedisServiceError error => httpProblems.Map(error).ToHttpResult()
                }))
            .WithName("DeleteRedisService")
            .Produces(StatusCodes.Status202Accepted);

        return app;
    }
}

public sealed record RedisDeletionResourceDeploymentMissing(string RedisId);

public union RedisDeletionStateError(
    RedisServiceNotFound,
    RedisDeletionResourceDeploymentMissing);

public union DeleteRedisServiceError(
    RedisDeletionStateError,
    LoadCurrentResourceDeploymentComputeError,
    RedisDeletionConflict,
    ResourceDeploymentMutationError,
    QuotaExecutionError);

public union DeleteRedisServiceOutcome(
    DeleteRedisServiceResponse,
    DeleteRedisServiceError);

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
            return Error(new DeleteRedisServiceError(
                new RedisDeletionStateError(new RedisServiceNotFound(request.Id))));
        }
        if (redis.Lifecycle is DeletingRedisLifecycle)
        {
            var deletingPlacement = await loadDeployment(
                request.TenantId,
                RedisResourceDeployment.ResourceType,
                request.Id,
                cancellationToken);
            return deletingPlacement is null
                ? Error(new DeleteRedisServiceError(
                    new RedisDeletionStateError(
                        new RedisDeletionResourceDeploymentMissing(redis.Id))))
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
            return Error(new DeleteRedisServiceError(currentComputeError));
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
            quotaError => Error(new DeleteRedisServiceError(quotaError)),
            async (transaction, transactionCancellationToken) =>
            {
                var deleting = redis.BeginDeletion(
                    timeProvider.GetUtcNow().UtcDateTime);
                var persistenceResult = await beginRedisDeletion(
                    transaction.Session,
                    redis,
                    deleting,
                    transactionCancellationToken);
                if (!persistenceResult.TryGetValue(out _, out var persistenceError))
                {
                    return transaction.Abort(Error(new DeleteRedisServiceError(
                        persistenceError)));
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
                    return transaction.Abort(Error(new DeleteRedisServiceError(
                        deploymentError)));
                }

                return transaction.Commit(new DeleteRedisServiceOutcome(
                    new DeleteRedisServiceResponse(request.Id, deployment)));
            },
            cancellationToken);
    }

    private static DeleteRedisServiceOutcome Error(DeleteRedisServiceError error) => new(error);
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
        if (!result.TryGetValue(out _, out var conflict))
        {
            throw new InvalidOperationException(
                $"Redis deletion finalization conflicted for '{conflict.RedisId}'.");
        }
    }
}

public sealed record DeleteRedisServiceRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId
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
