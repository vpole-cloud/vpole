using System.Collections.Immutable;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.Core.Multitenancy;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Persistence;
using Pole.Features.RedisServices.Domain;
using Pole.Features.Quotas.Contracts;
using Pole.Features.RedisServices.Application.Persistence.Mutations;
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
            app.MapPost("/", async (
                    [FromServices] IMediator mediator,
                    [FromServices] IHttpProblemMapper httpProblems,
                    [AsParameters] CreateRedisServiceRequest request,
                    CancellationToken cancellationToken) =>
                    (await mediator.Send(request, cancellationToken))
                    .ToHttpResult(outcome => outcome switch
                    {
                        CreateRedisServiceResponse response => Results.Accepted(value: response),
                        CreateRedisServiceError error => httpProblems.Map(error).ToHttpResult()
                    }))
                .WithName("CreateRedisService")
                .Produces<CreateRedisServiceResponse>(StatusCodes.Status202Accepted);

            return app;
        }
    }
}

public union CreateRedisServiceError(
    DeploymentTargetNewWorkloadAdmissionError,
    ComputeProfileResolutionError,
    StorageProfileResolutionError,
    CreateResourceDeploymentError,
    ResourceRegistrationMutationError,
    QuotaExecutionError);

public union CreateRedisServiceOutcome(
    CreateRedisServiceResponse,
    CreateRedisServiceError);

public sealed class CreateRedisServiceHandler(
    PersistCreatedRedis persistCreatedRedis,
    ResolveStorageProfile resolveStorageProfile,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    AdmitDeploymentTargetForNewWorkload admitDeploymentTarget,
    ResolveComputeProfile resolveComputeProfile,
    CreateResourceDeployment createResourceDeployment,
    IResourceRegistrar resourceRegistrar)
    : IMediatorOutcomeHandler<CreateRedisServiceRequest, CreateRedisServiceOutcome>
{
    public async Task<CreateRedisServiceOutcome> HandleAsync(CreateRedisServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var targetResult = await admitDeploymentTarget(
            new DeploymentTargetAdmissionRequest(
                request.TenantId,
                RedisResourceDeployment.ResourceType,
                request.Body.Placement?.IsolationScopeId,
                request.Body.Placement?.DeploymentTargetId,
                DeploymentTargetRequiredCapabilities.None),
            cancellationToken);
        if (!targetResult.TryGetValue(out var target, out var targetError))
            return Error(new CreateRedisServiceError(targetError));

        var computeProfileResult = await resolveComputeProfile(
            new ComputeProfileResolutionRequest(
                request.Body.ComputeProfileId,
                target.Id,
                request.TenantId,
                RedisResourceDeployment.ResourceType),
            cancellationToken);
        if (computeProfileResult is ComputeProfileResolutionError computeProfileError)
        {
            return Error(new CreateRedisServiceError(computeProfileError));
        }

        var computeProfile = (ResolvedComputeProfile)computeProfileResult.Value;

        var storageProfileResult = await resolveStorageProfile(
            new StorageProfileResolutionRequest(
                request.Body.Spec.Storage.StorageProfileId,
                target.Id,
                request.TenantId,
                RedisResourceDeployment.ResourceType,
                request.Body.Spec.Storage.SizeGb),
            cancellationToken);
        if (!storageProfileResult.TryGetValue(out var storageProfile, out var storageProfileError))
            return Error(new CreateRedisServiceError(storageProfileError));

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
        return await quotaAdmissionExecutor.ExecuteOutcomeAsync<
            RedisQuotaInput,
            CreateRedisServiceOutcome>(
            request.TenantId,
            RedisQuotaResourceKey.Create(newRedis.Id),
            new CreateQuotaMutation<RedisQuotaInput>(quotaInput),
            quotaError => Error(new CreateRedisServiceError(quotaError)),
            async (transaction, transactionCancellationToken) =>
            {
                await persistCreatedRedis(
                    transaction.Session,
                    newRedis,
                    transactionCancellationToken);

                var registration = await resourceRegistrar.RegisterAsync(
                    newRedis.TenantId,
                    newRedis.ResourceGroupId,
                    new ResourceRegistrationDescriptor(
                        RedisResourceDeployment.ResourceType,
                        newRedis.Id,
                        newRedis.Name),
                    transaction.Session,
                    transactionCancellationToken);
                if (!registration.TryGetValue(out _, out var registrationError))
                {
                    return transaction.Abort(
                        Error(new CreateRedisServiceError(registrationError)));
                }

                var deploymentResult = await createResourceDeployment(
                    new CreateResourceDeploymentRequest(
                        deploymentId,
                        request.TenantId,
                        RedisResourceDeployment.ResourceType,
                        newRedis.Id,
                        newRedis.ResourceGroupId,
                        request.Body.Placement?.IsolationScopeId,
                        ToResolvedResourceDeploymentTarget(target),
                        computeProfile.ToResourceDeploymentConfiguration(),
                        resolvedStorage,
                        ResourcePlacementCapability.SingleTarget),
                    transaction.Session,
                    transactionCancellationToken);
                if (!deploymentResult.TryGetValue(
                        out var deployment,
                        out var deploymentError))
                {
                    return transaction.Abort(Error(new CreateRedisServiceError(deploymentError)));
                }

                return transaction.Commit(new CreateRedisServiceOutcome(
                    new CreateRedisServiceResponse(newRedis.Id, deployment)));
            },
            cancellationToken);
    }

    private static CreateRedisServiceOutcome Error(CreateRedisServiceError error) => new(error);

    private static ResolvedResourceDeploymentTarget ToResolvedResourceDeploymentTarget(
        DeploymentTargetLookupDto target) =>
        new(
            target.Id,
            target.WorkloadConfiguration.Revision,
            target.WorkloadConfiguration.Ingress is { } ingress
                ? ResourceDeploymentIngressConfiguration.Create(
                    ingress.IngressClassName,
                    ingress.ClusterIssuerName,
                    ingress.PublicBaseDomain,
                    [])
                : null);
}

public sealed record CreateRedisServiceRequest(
    [FromBody] CreateRedisServiceRequestBody Body,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<CreateRedisServiceOutcome>, ITenantCreateOperation;

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
            .NotEmpty()
            .MongoObjectId("Resource group ID");
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
