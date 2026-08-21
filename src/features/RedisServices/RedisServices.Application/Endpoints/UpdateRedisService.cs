using System.Collections.Immutable;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.Quotas.Contracts;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Persistence;
using Pole.Features.RedisServices.Domain;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

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
    ResolveComputeProfileLegacy resolveComputeProfile,
    LoadCurrentResourceDeploymentComputeLegacy loadDeploymentCompute,
    RequestResourceDeploymentApplyLegacy requestDeploymentApply)
    : IMediatorHandler<UpdateRedisServiceRequest, UpdateRedisServiceResponse>
{
    public async Task<Result<UpdateRedisServiceResponse>> HandleAsync(
        UpdateRedisServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var redis = await loadRedisById(
            request.TenantId,
            request.Id,
            cancellationToken);

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
            if (!computeResult.TryGetValue(
                    out replacementCompute,
                    out var computeFailure))
            {
                return computeFailure;
            }
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
        return await quotaAdmissionExecutor.ExecuteAsync<RedisQuotaInput, UpdateRedisServiceResponse>(
            redis.TenantId,
            RedisQuotaResourceKey.Create(redis.Id),
            new UpdateQuotaMutation<RedisQuotaInput>(
                currentQuotaInput,
                nextQuotaInput),
            async (session, transactionCancellationToken) =>
            {
                var updatedRedis = redis.UpdateDesiredState(
                    nextSpec,
                    request.Body.Tags);
                var persistenceResult = await persistUpdatedRedis(
                    session,
                    redis,
                    updatedRedis,
                    transactionCancellationToken);
                if (persistenceResult is Failure persistenceFailure)
                    return persistenceFailure;

                var deploymentResult = await requestDeploymentApply(
                    new ResourceDeploymentMutationRequest(
                        redis.TenantId,
                        RedisResourceDeployment.ResourceType,
                        redis.Id,
                        currentCompute.Compute.Profile,
                        replacementCompute?.ToResourceDeploymentConfiguration()),
                    session,
                    transactionCancellationToken);
                if (!deploymentResult.TryGetValue(
                        out var deployment,
                        out var deploymentFailure))
                {
                    return deploymentFailure;
                }

                return new UpdateRedisServiceResponse(
                    redis.Id,
                    deployment);
            },
            cancellationToken);
    }
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
        RuleFor(x => x.Body.ComputeProfileId)
            .OptionalMongoObjectId("Compute profile ID");
    }
}
