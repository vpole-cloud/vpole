using System.Collections.Immutable;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Application.Placement;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Domain;
using Pole.Features.RedisServices.Events;
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
    IRedisDbContext dbContext,
    IStorageProfileResolver storageProfileResolver,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    IResourceDeploymentPlacementResolver placementResolver,
    IResourceDeploymentWriter deploymentWriter)
    : IMediatorHandler<CreateRedisServiceRequest, CreateRedisServiceResponse>
{
    public async Task<Result<CreateRedisServiceResponse>> HandleAsync(CreateRedisServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var targetResult = await placementResolver.ResolveTargetAsync(
            request.Body.Placement?.DeploymentTargetId,
            requiresIngress: false,
            cancellationToken);
        if (!targetResult.TryGetValue(out var target, out var targetFailure))
            return targetFailure;

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
        var resolvedStorage = ResolvedStorageConfiguration.Create(
            storageProfile.ProfileId,
            storageProfile.ProfileRevision,
            storageProfile.StorageClassName);

        return await quotaAdmissionExecutor.ExecuteAsync<RedisSpec, CreateRedisServiceResponse>(
            request.TenantId,
            RedisQuotaResourceKey.Create(newRedis.Id),
            new CreateQuotaMutation<RedisSpec>(newRedis.Spec),
            async (session, transactionCancellationToken) =>
            {
                await dbContext.RedisServices.InsertOneAsync(
                    session,
                    newRedis,
                    cancellationToken: transactionCancellationToken);

                var deploymentResult = await deploymentWriter.CreateAsync(
                    new CreateResourceDeploymentCommand(
                        deploymentId,
                        request.TenantId,
                        ResourceDeploymentResourceTypes.Redis,
                        newRedis.Id,
                        newRedis.ResourceGroupId,
                        request.Body.Placement?.IsolationScopeId,
                        target,
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

                await dbContext.StoreEventAsync(
                    newRedis.ToCreateEvent(),
                    session,
                    transactionCancellationToken);

                return new CreateRedisServiceResponse(
                    newRedis.Id,
                    deployment.ToPlacementSummaryDto());
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
        When(body => body.Placement is not null, () =>
        {
            RuleFor(body => body.Placement!.IsolationScopeId)
                .OptionalMongoObjectId("Isolation scope ID");
            RuleFor(body => body.Placement!.DeploymentTargetId)
                .OptionalMongoObjectId("Deployment target ID");
        });
    }
}
