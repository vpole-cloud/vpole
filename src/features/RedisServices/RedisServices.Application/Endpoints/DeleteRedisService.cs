using Pole.Features.RedisServices.Application.Persistence.Mutations;
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
                    RedisSecretBindingRemovalRequired error => MapSecretBindingRemovalRequired(error),
                    RedisGitOwnedConfiguration error => MapGitOwned(error),
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
    RedisGitOwnedConfiguration,
    RedisSecretBindingRemovalRequired,
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
                new LoadResourceDeploymentRequest(
                    TenantId: request.TenantId,
                    ResourceType: RedisResourceDeployment.ResourceType,
                    ResourceId: request.Id),
                cancellationToken: cancellationToken);
            return deletingPlacement is null
                ? new RedisDeletionResourceDeploymentMissing(redis.Id)
                : new DeleteRedisServiceOutcome(
                    new DeleteRedisServiceResponse(request.Id, deletingPlacement));
        }

        var currentComputeResult = await loadDeploymentCompute(
            new LoadCurrentResourceDeploymentComputeRequest(
                TenantId: redis.TenantId,
                ResourceType: RedisResourceDeployment.ResourceType,
                ResourceId: redis.Id),
            cancellationToken: cancellationToken);
        if (currentComputeResult is LoadCurrentResourceDeploymentComputeError currentComputeError)
        {
            return currentComputeError;
        }
        var currentCompute = (CurrentResourceDeploymentComputeDto)currentComputeResult.Value;

        var quotaInput = RedisQuotaInput.Create(
            currentCompute.Compute.Requests.CpuMillis,
            currentCompute.Compute.Requests.MemoryMi);
        return await quotaAdmissionExecutor.ExecuteOutcomeAsync<
            RedisQuotaInput,
            DeleteRedisServiceOutcome>(
            new ExecuteQuotaAdmissionRequest<global::Pole.Features.RedisServices.Application.Common.RedisQuotaInput,
                global::Pole.Features.RedisServices.Application.Endpoints.DeleteRedisServiceOutcome>(
                TenantId: redis.TenantId,
                ResourceKey: RedisQuotaResourceKey.Create(redis.Id),
                Mutation: new DeleteQuotaMutation<RedisQuotaInput>(quotaInput),
                QuotaError: quotaError => quotaError,
                MutationCallback: async (transaction, transactionCancellationToken) =>
                {
                    var deleting = redis.BeginDeletion(
                        timeProvider.GetUtcNow().UtcDateTime);
                    var persistenceResult = await beginRedisDeletion(
                        new(redis, deleting),
                        transaction.Session,
                        transactionCancellationToken);
                    if (persistenceResult is RedisSecretBindingRemovalRequired bindingError)
                        return transaction.Abort(bindingError);
                    if (persistenceResult is RedisGitOwnedConfiguration ownershipError)
                        return transaction.Abort(ownershipError);

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
                    if (deploymentResult is ResourceDeploymentMutationError deploymentError)
                    {
                        return transaction.Abort(deploymentError);
                    }
                    var deployment = (PlacementSummaryDto)deploymentResult.Value;

                    return transaction.Commit(new DeleteRedisServiceOutcome(
                        new DeleteRedisServiceResponse(request.Id, deployment)));
                }),
            cancellationToken: cancellationToken);
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
