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
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Quotas.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Services;
using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.DeploymentTargets.Contracts.HttpMapping.DeploymentTargetContractHttpMappings;
using static Pole.Features.Quotas.Contracts.HttpMapping.QuotaContractHttpMappings;
using static Pole.Features.ResourceDeployments.Contracts.HttpMapping.ResourceDeploymentContractHttpMappings;
using static Pole.Features.ResourceGroups.Contracts.HttpMapping.ResourceGroupContractHttpMappings;
using ComputeHttpMappings = Pole.Features.ComputeProfiles.Contracts.HttpMapping.ComputeProfileContractHttpMappings;
using StorageHttpMappings = Pole.Features.StorageProfiles.Contracts.HttpMapping.StorageProfileContractHttpMappings;

namespace Pole.Features.RedisServices.Application.Endpoints;

public static class CreateRedisService
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapCreateRedisServiceEndpoint()
        {
            app.MapPost(
                    "/",
                    async (
                            [FromServices] IMediator mediator, [AsParameters] CreateRedisServiceRequest request,
                            CancellationToken cancellationToken) =>
                        (await mediator.Send(request, cancellationToken)).ToHttpResult(outcome => outcome switch
                        {
                            CreateRedisServiceResponse response => Results.Accepted(value: response),
                            RedisServiceNameAlreadyExists error => RedisHttpMappings.MapNameAlreadyExists(error),
                            DeploymentTargetNewWorkloadAdmissionError error => MapNewWorkloadAdmissionError(error),
                            ComputeProfileResolutionError error => ComputeHttpMappings.MapResolutionError(error),
                            StorageProfileResolutionError error => StorageHttpMappings.MapResolutionError(error),
                            CreateResourceDeploymentError error => MapDeploymentError(error),
                            ResourceRegistrationMutationError error => MapRegistrationMutationError(error),
                            QuotaExecutionError error => MapExecutionError(error),
                        }))
                .WithName("CreateRedisService")
                .Produces<CreateRedisServiceResponse>(StatusCodes.Status202Accepted);

            return app;
        }
    }
}

public union CreateRedisServiceOutcome(
    CreateRedisServiceResponse,
    RedisServiceNameAlreadyExists,
    DeploymentTargetNewWorkloadAdmissionError,
    ComputeProfileResolutionError,
    StorageProfileResolutionError,
    CreateResourceDeploymentError,
    ResourceRegistrationMutationError,
    QuotaExecutionError);

public sealed class CreateRedisServiceHandler(
    PersistCreatedRedis persistCreatedRedis,
    ResolveStorageProfile resolveStorageProfile,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    AdmitDeploymentTargetForNewWorkload admitDeploymentTarget,
    ResolveComputeProfile resolveComputeProfile,
    CreateResourceDeployment createResourceDeployment,
    IResourceRegistrar resourceRegistrar
) : IMediatorOutcomeHandler<CreateRedisServiceRequest, CreateRedisServiceOutcome>
{
    public async Task<CreateRedisServiceOutcome> HandleAsync(
        CreateRedisServiceRequest request,
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
        if (targetResult is DeploymentTargetNewWorkloadAdmissionError targetError)
            return targetError;
        var target = (DeploymentTargetLookupDto)targetResult.Value;

        var computeProfileResult = await resolveComputeProfile(
            new ComputeProfileResolutionRequest(
                request.Body.ComputeProfileId,
                target.Id,
                request.TenantId,
                RedisResourceDeployment.ResourceType),
            cancellationToken);
        if (computeProfileResult is ComputeProfileResolutionError computeProfileError)
            return computeProfileError;

        var computeProfile = (ResolvedComputeProfile)computeProfileResult.Value;

        var storageProfileResult = await resolveStorageProfile(
            new StorageProfileResolutionRequest(
                request.Body.Spec.Storage.StorageProfileId,
                target.Id,
                request.TenantId,
                RedisResourceDeployment.ResourceType,
                request.Body.Spec.Storage.SizeGb),
            cancellationToken);
        if (storageProfileResult is StorageProfileResolutionError storageProfileError)
            return storageProfileError;

        var storageProfile = (ResolvedStorageProfile)storageProfileResult.Value;
        var createOptions = RedisFactory.DefaultCreateOptions with
        {
            Tags = request.Body.Tags,
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

        var quotaInput = RedisQuotaInput.Create(computeProfile.Requests.CpuMillis, computeProfile.Requests.MemoryMi);
        return await quotaAdmissionExecutor.ExecuteOutcomeAsync<RedisQuotaInput, CreateRedisServiceOutcome>(
            new ExecuteQuotaAdmissionRequest<RedisQuotaInput, CreateRedisServiceOutcome>(
                TenantId: request.TenantId,
                ResourceKey: RedisQuotaResourceKey.Create(newRedis.Id),
                Mutation: new CreateQuotaMutation<RedisQuotaInput>(quotaInput),
                QuotaError: quotaError => quotaError,
                MutationCallback: async (transaction, transactionCancellationToken) =>
                {
                    var persisted = await persistCreatedRedis(
                        transaction.Session,
                        newRedis,
                        transactionCancellationToken);
                    if (persisted is RedisServiceNameAlreadyExists nameConflict)
                        return transaction.Abort(new CreateRedisServiceOutcome(nameConflict));

                    var registration = await resourceRegistrar.RegisterAsync(
                        new RegisterResourceRequest(
                            TenantId: newRedis.TenantId,
                            ResourceGroupId: newRedis.ResourceGroupId,
                            Resource: new ResourceRegistrationDescriptor(
                                RedisResourceDeployment.ResourceType,
                                newRedis.Id,
                                newRedis.Name)),
                        session: transaction.Session,
                        cancellationToken: transactionCancellationToken);
                    if (registration is ResourceRegistrationMutationError registrationError)
                        return transaction.Abort(registrationError);

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
                    if (deploymentResult is CreateResourceDeploymentError deploymentError)
                        return transaction.Abort(deploymentError);
                    var deployment = (PlacementSummaryDto)deploymentResult.Value;

                    return transaction.Commit(
                        new CreateRedisServiceOutcome(new CreateRedisServiceResponse(newRedis.Id, deployment)));
                }),
            cancellationToken: cancellationToken);
    }

    private static ResolvedResourceDeploymentTarget
        ToResolvedResourceDeploymentTarget(DeploymentTargetLookupDto target) => new(
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
        RuleFor(x => x.ResourceGroupId).NotEmpty().MongoObjectId("Resource group ID");
        RuleFor(x => x.Spec).SetValidator(new RedisSpecDtoValidator());
        RuleFor(body => body.ComputeProfileId).OptionalMongoObjectId("Compute profile ID");
        When(
            body => body.Placement is not null,
            () =>
            {
                RuleFor(body => body.Placement!.IsolationScopeId).OptionalMongoObjectId("Isolation scope ID");
                RuleFor(body => body.Placement!.DeploymentTargetId).OptionalMongoObjectId("Deployment target ID");
            });
    }
}
