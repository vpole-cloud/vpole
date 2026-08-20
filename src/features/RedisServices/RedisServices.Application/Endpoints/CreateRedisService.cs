using System.Collections.Immutable;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Persistence;
using Pole.Features.RedisServices.Domain;
using Pole.Features.Quotas.Contracts;
using Pole.Features.Shared.Models;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

namespace Pole.Features.RedisServices.Application.Endpoints;

public static class CreateRedisService
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapCreateRedisServiceEndpoint()
        {
            app.MapPost("/", async ([FromServices] IMediator mediator, [AsParameters] CreateRedisServiceRequest request) =>
                {
                    var response = await mediator.Send(request);
                    return response.ToHttpResult(v => Results.Accepted(value: v));
                })
                .WithName("CreateRedisService")
                .Produces<CreateRedisServiceResponse>(StatusCodes.Status202Accepted);

            return app;
        }
    }
}

public sealed class CreateRedisServiceHandler(
    PersistCreatedRedis persistCreatedRedis,
    IStorageProfileResolver storageProfileResolver,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    ResolveResourceDeploymentTargetLegacy resolveResourceDeploymentTarget,
    ResolveComputeProfile resolveComputeProfile,
    CreateResourceDeploymentLegacy createResourceDeployment)
    : IMediatorHandler<CreateRedisServiceRequest, CreateRedisServiceResponse>
{
    public async Task<Result<CreateRedisServiceResponse>> HandleAsync(CreateRedisServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var targetResult = await resolveResourceDeploymentTarget(
            request.Body.Placement?.DeploymentTargetId,
            requiresIngress: false,
            cancellationToken);
        if (!targetResult.TryGetValue(out var target, out var targetFailure))
            return targetFailure;

        var computeProfileResult = await resolveComputeProfile(
            new ComputeProfileResolutionRequest(
                request.Body.ComputeProfileId,
                target.DeploymentTargetId,
                request.TenantId,
                RedisResourceDeployment.ResourceType),
            cancellationToken);
        if (!computeProfileResult.TryGetValue(
                out var computeProfile,
                out var computeProfileFailure))
        {
            return computeProfileFailure;
        }

        var storageProfileResult = await storageProfileResolver.ResolveAsync(
            new StorageProfileResolutionRequest(
                request.Body.Spec.Storage.StorageProfileId,
                target.DeploymentTargetId,
                request.TenantId,
                ResourceType.Redis,
                request.Body.Spec.Storage.SizeGb),
            cancellationToken);
        if (!storageProfileResult.TryGetValue(out var storageProfile, out var storageProfileFailure))
            return storageProfileFailure;

        var createOptions = RedisFactory.DefaultCreateOptions with
        {
            Tags = request.Body.Tags
        };
        var newRedis = RedisFactory.Create(
            tenantId: request.TenantId,
            name: request.Body.Name,
            resourceGroupId: request.Body.ResourceGroupId,
            spec: request.Body.Spec.ToDomain(),
            options: createOptions);
        var deploymentId = ObjectId.GenerateNewId().ToString();
        var resolvedStorage = new ResourceDeploymentStorageConfiguration(
            storageProfile.ProfileId,
            storageProfile.ProfileRevision,
            storageProfile.StorageClassName);

        var quotaInput = RedisQuotaInput.Create(
            computeProfile.Requests.CpuMillis,
            computeProfile.Requests.MemoryMi);
        return await quotaAdmissionExecutor.ExecuteAsync<RedisQuotaInput, CreateRedisServiceResponse>(
            request.TenantId,
            RedisQuotaResourceKey.Create(newRedis.Id),
            new CreateQuotaMutation<RedisQuotaInput>(quotaInput),
            async (session, transactionCancellationToken) =>
            {
                await persistCreatedRedis(
                    session,
                    newRedis,
                    transactionCancellationToken);

                var deploymentResult = await createResourceDeployment(
                    new CreateResourceDeploymentRequest(
                        deploymentId,
                        request.TenantId,
                        RedisResourceDeployment.ResourceType,
                        newRedis.Id,
                        newRedis.ResourceGroupId,
                        request.Body.Placement?.IsolationScopeId,
                        target,
                        computeProfile.ToResourceDeploymentConfiguration(),
                        resolvedStorage,
                        ResourcePlacementCapability.SingleTarget),
                    session,
                    transactionCancellationToken);
                if (!deploymentResult.TryGetValue(
                        out var deployment,
                        out var deploymentFailure))
                {
                    return deploymentFailure;
                }

                return new CreateRedisServiceResponse(
                    newRedis.Id,
                    deployment);
            },
            cancellationToken);
    }
}

public sealed record CreateRedisServiceRequest(
    [FromBody] CreateRedisServiceRequestBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId
) : IMediatorRequest<CreateRedisServiceResponse>, ITenantCreateOperation;

public sealed record CreateRedisServiceRequestBody(
    string Name,
    string ResourceGroupId,
    RedisSpecDto Spec,
    ImmutableHashSet<string> Tags,
    string? ComputeProfileId = null,
    PlacementRequest? Placement = null
);

public sealed record CreateRedisServiceResponse(
    string RedisId,
    PlacementSummaryDto Placement
);

public sealed class CreateRedisServiceRequestValidator : AbstractValidator<CreateRedisServiceRequest>
{
    public CreateRedisServiceRequestValidator()
    {
        RuleFor(x => x.Body).SetValidator(new CreateRedisServiceRequestBodyValidator());
    }
}

public sealed class CreateRedisServiceRequestBodyValidator : AbstractValidator<CreateRedisServiceRequestBody>
{
    public CreateRedisServiceRequestBodyValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(30)
            .Matches("^[a-z0-9-]+$")
            .WithMessage("Name must contain only lowercase letters, numbers, and hyphens.");
        RuleFor(x => x.ResourceGroupId)
            .NotEmpty();
        RuleFor(x => x.Spec)
            .SetValidator(new RedisSpecDtoValidator());
        RuleFor(body => body.ComputeProfileId)
            .OptionalMongoObjectId("Compute profile ID");
        When(body => body.Placement is not null, () =>
        {
            RuleFor(body => body.Placement!.IsolationScopeId)
                .OptionalMongoObjectId("Isolation scope ID");
            RuleFor(body => body.Placement!.DeploymentTargetId)
                .OptionalMongoObjectId("Deployment target ID");
        });
    }
}
