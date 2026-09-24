using System.Collections.Immutable;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.Quotas.Contracts;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Persistence;
using Pole.Features.RedisServices.Application.Persistence.Mutations;
using Pole.Features.RedisServices.Application.Persistence.Reads;
using Pole.Features.RedisServices.Domain;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;
using static Pole.Features.RedisServices.Application.Common.RedisHttpMappings;
using static Pole.Features.ResourceDeployments.Contracts.HttpMapping.ResourceDeploymentContractHttpMappings;
using static Pole.Features.Quotas.Contracts.HttpMapping.QuotaContractHttpMappings;
using ComputeHttpMappings = Pole.Features.ComputeProfiles.Contracts.HttpMapping.ComputeProfileContractHttpMappings;

namespace Pole.Features.RedisServices.Application.Endpoints;

public static class UpdateRedisService
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapUpdateRedisServiceEndpoint()
        {
            app.MapPut("/{Id}", async (
                    IMediator mediator,
                    [AsParameters] UpdateRedisServiceRequest request,
                    CancellationToken cancellationToken) =>
                    (await mediator.Send(request, cancellationToken))
                    .ToHttpResult(outcome => outcome switch
                    {
                        UpdateRedisServiceResponse response => Results.Accepted(value: response),
                        RedisServiceNotFound error => MapNotFound(error),
                        RedisUpdateUnavailable error => MapUpdateUnavailable(error),
                        RedisStorageProfileChangeNotAllowed error => MapStorageProfileChangeNotAllowed(error),
                        RedisUpdateConflict error => MapUpdateConflict(error),
                        LoadCurrentResourceDeploymentComputeError error => MapComputeError(error),
                        ComputeProfileResolutionError error => ComputeHttpMappings.MapResolutionError(error),
                        ResourceDeploymentMutationError error => MapMutationError(error),
                        QuotaExecutionError error => MapExecutionError(error)
                    }))
                .WithName("UpdateRedisService")
                .Produces<UpdateRedisServiceResponse>(StatusCodes.Status202Accepted);

            return app;
        }
    }
}

public sealed record RedisServiceNotFound(string RedisId);

public sealed record RedisUpdateUnavailable(string RedisId);

public sealed record RedisStorageProfileChangeNotAllowed(string RedisId);

public union UpdateRedisServiceOutcome(
    UpdateRedisServiceResponse,
    RedisServiceNotFound,
    RedisUpdateUnavailable,
    RedisStorageProfileChangeNotAllowed,
    LoadCurrentResourceDeploymentComputeError,
    ComputeProfileResolutionError,
    RedisUpdateConflict,
    ResourceDeploymentMutationError,
    QuotaExecutionError);

public sealed record UpdateRedisServiceRequest(
    [FromRoute] string Id,
    [FromBody] UpdateRedisServiceRequestBody Body,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<UpdateRedisServiceOutcome>, ITenantWriteOperation;

public sealed record UpdateRedisServiceRequestBody(
    RedisSpecDto Spec,
    ImmutableHashSet<string> Tags,
    string? ComputeProfileId = null
);

public sealed record UpdateRedisServiceResponse(
    string Id,
    PlacementSummaryDto Placement);

public sealed class UpdateRedisServiceHandler(
    LoadRedisById loadRedisById,
    PersistUpdatedRedis persistUpdatedRedis,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    ResolveComputeProfile resolveComputeProfile,
    LoadCurrentResourceDeploymentCompute loadDeploymentCompute,
    RequestResourceDeploymentApply requestDeploymentApply)
    : IMediatorOutcomeHandler<UpdateRedisServiceRequest, UpdateRedisServiceOutcome>
{
    public async Task<UpdateRedisServiceOutcome> HandleAsync(
        UpdateRedisServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var redis = await loadRedisById(
            request.TenantId,
            request.Id,
            cancellationToken);

        if (redis is null)
            return new RedisServiceNotFound(request.Id);
        if (redis.Lifecycle is not ActiveRedisLifecycle)
        {
            return new RedisUpdateUnavailable(redis.Id);
        }

        if (!string.Equals(
                request.Body.Spec.Storage.StorageProfileId,
                redis.Spec.Storage.StorageProfileId,
                StringComparison.Ordinal))
        {
            return new RedisStorageProfileChangeNotAllowed(redis.Id);
        }

        var nextSpec = request.Body.Spec.ToDomain();
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

        ResolvedComputeProfile? replacementCompute = null;
        if (!string.IsNullOrWhiteSpace(request.Body.ComputeProfileId))
        {
            var computeResult = await resolveComputeProfile(
                new ComputeProfileResolutionRequest(
                    request.Body.ComputeProfileId,
                    currentCompute.DeploymentTargetId,
                    redis.TenantId,
                    RedisResourceDeployment.ResourceType),
                cancellationToken);
            if (computeResult is ComputeProfileResolutionError computeError)
            {
                return computeError;
            }

            replacementCompute = (ResolvedComputeProfile)computeResult.Value;
        }

        var currentQuotaInput = RedisQuotaInput.Create(
            currentCompute.Compute.Requests.CpuMillis,
            currentCompute.Compute.Requests.MemoryMi);
        var nextQuotaInput = replacementCompute is null
            ? RedisQuotaInput.Create(
                currentCompute.Compute.Requests.CpuMillis,
                currentCompute.Compute.Requests.MemoryMi)
            : RedisQuotaInput.Create(
                replacementCompute.Requests.CpuMillis,
                replacementCompute.Requests.MemoryMi);
        return await quotaAdmissionExecutor.ExecuteOutcomeAsync<
            RedisQuotaInput,
            UpdateRedisServiceOutcome>(
            redis.TenantId,
            RedisQuotaResourceKey.Create(redis.Id),
            new UpdateQuotaMutation<RedisQuotaInput>(
                currentQuotaInput,
                nextQuotaInput),
            quotaError => quotaError,
            async (transaction, transactionCancellationToken) =>
            {
                var updatedRedis = redis.UpdateDesiredState(
                    nextSpec,
                    request.Body.Tags);
                var persistenceResult = await persistUpdatedRedis(
                    transaction.Session,
                    redis,
                    updatedRedis,
                    transactionCancellationToken);
                if (persistenceResult is RedisUpdateConflict persistenceError)
                {
                    return transaction.Abort(persistenceError);
                }

                var deploymentResult = await requestDeploymentApply(
                    new ResourceDeploymentMutationRequest(
                        redis.TenantId,
                        RedisResourceDeployment.ResourceType,
                        redis.Id,
                        currentCompute.Compute.Profile,
                        replacementCompute?.ToResourceDeploymentConfiguration()),
                    transaction.Session,
                    transactionCancellationToken);
                if (!deploymentResult.TryGetValue(
                        out var deployment,
                        out var deploymentError))
                {
                    return transaction.Abort(deploymentError);
                }

                return transaction.Commit(new UpdateRedisServiceOutcome(
                    new UpdateRedisServiceResponse(redis.Id, deployment)));
            },
            cancellationToken);
    }
}

public sealed class UpdateRedisServiceValidator : AbstractValidator<UpdateRedisServiceRequest>
{
    public UpdateRedisServiceValidator()
    {
        RuleFor(x => x.Id)
            .NotEmpty()
            .MongoObjectId("Redis service ID");

        RuleFor(x => x.Body.Spec)
            .NotNull().WithMessage("Redis service specification is required.")
            .SetValidator(new RedisSpecDtoValidator());
        RuleFor(x => x.Body.ComputeProfileId)
            .OptionalMongoObjectId("Compute profile ID");
    }
}
