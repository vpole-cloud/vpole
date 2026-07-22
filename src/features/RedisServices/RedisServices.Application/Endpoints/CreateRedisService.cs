using System.Collections.Immutable;
using FluentValidation;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Domain;
using Pole.Features.RedisServices.Events;
using Pole.Features.RedisServices.Application.Kubernetes;
using Pole.Features.Quotas.Contracts;
using Pole.Features.Shared.Models;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Mediator;
using Pole.ResultType;

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
    IResourceGroupLookup resourceGroupLookup,
    IStorageProfileResolver storageProfileResolver,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    IDeploymentTargetLookup deploymentTargetLookup)
    : IMediatorHandler<CreateRedisServiceRequest, CreateRedisServiceResponse>
{
    public async Task<Result<CreateRedisServiceResponse>> HandleAsync(CreateRedisServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var targetAdmission = await deploymentTargetLookup.ResolveForNewWorkloadAsync(
            deploymentTargetId: null,
            cancellationToken);
        if (!targetAdmission.TryGetValue(out _, out var targetAdmissionFailure))
            return targetAdmissionFailure;

        var placementResult = await resourceGroupLookup.GetPlacementAsync(
            request.TenantId,
            request.Body.ResourceGroupId,
            cancellationToken);
        if (!placementResult.TryGetValue(out var placement, out var placementFailure))
            return placementFailure;

        var storageProfileResult = await storageProfileResolver.ResolveAsync(
            new StorageProfileResolutionRequest(
                request.Body.Spec.Storage.StorageProfileId,
                request.TenantId,
                ResourceType.Redis,
                request.Body.Spec.Storage.SizeGb),
            cancellationToken);
        if (!storageProfileResult.TryGetValue(out var storageProfile, out var storageProfileFailure))
            return storageProfileFailure;

        var newRedis = RedisFactory.Create(
            tenantId: request.TenantId,
            name: request.Body.Name,
            resourceGroupId: request.Body.ResourceGroupId,
            spec: request.Body.Spec.ToDomain(placement.KubernetesNamespace, storageProfile),
            options: RedisFactory.DefaultCreateOptions with
            {
                Tags = request.Body.Tags
            }
        );

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
                await dbContext.StoreEventAsync(
                    newRedis.ToCreateEvent(),
                    session,
                    transactionCancellationToken);

                return new CreateRedisServiceResponse(newRedis.Id);
            },
            cancellationToken);
    }
}

public class ProvisionRedisServiceOnRedisServiceCreatedConsumer(
    IKubernetesClientProvider kubernetesClientProvider)
    : IConsumer<RedisServiceCreatedEvent>
{
    public async Task Consume(ConsumeContext<RedisServiceCreatedEvent> context)
    {
        var ct = context.CancellationToken;
        var redis = context.Message.ToRedis();
        var inKuber = redis.ToKubernetes();
        var kubernetes = await kubernetesClientProvider.GetDefaultAsync(ct);

        await RedisKubernetesResourceManager.CreateAsync(kubernetes, inKuber, ct);
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
    ImmutableHashSet<string> Tags
);

public sealed record CreateRedisServiceResponse(
    string RedisId
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
    }
}

