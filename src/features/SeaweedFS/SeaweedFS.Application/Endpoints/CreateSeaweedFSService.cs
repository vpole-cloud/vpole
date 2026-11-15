using Pole.Features.Postgres.Contracts;
using Pole.Features.Secrets.Contracts;
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
using Pole.Features.SeaweedFS.Application.Common;
using Pole.Features.SeaweedFS.Application.Services;
using Pole.Features.SeaweedFS.Domain;
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

namespace Pole.Features.SeaweedFS.Application.Endpoints;

public static class CreateSeaweedFSService
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapCreateSeaweedFSServiceEndpoint()
        {
            app.MapPost(
                    "/",
                    async (
                            [FromServices] IMediator mediator, [AsParameters] CreateSeaweedFSServiceRequest request,
                            CancellationToken cancellationToken) =>
                        (await mediator.Send(request, cancellationToken)).ToHttpResult(outcome => outcome switch
                        {
                            CreateSeaweedFSServiceResponse response => Results.Accepted(value: response),
                            SeaweedFSServiceNameAlreadyExists error =>
                                SeaweedFSHttpMappings.MapNameAlreadyExists(error),
                            DeploymentTargetNewWorkloadAdmissionError error => MapNewWorkloadAdmissionError(error),
                            SeaweedFSRuntimeClassUnsupported => SeaweedFSHttpMappings.Problem(
                                422,
                                "runtime-class-unsupported",
                                "Select a compute profile without a runtime class for SeaweedFS"),
                            ComputeProfileResolutionError error => ComputeHttpMappings.MapResolutionError(error),
                            StorageProfileResolutionError error => StorageHttpMappings.MapResolutionError(error),
                            CreateResourceDeploymentError error => MapDeploymentError(error),
                            ResourceRegistrationMutationError error => MapRegistrationMutationError(error),
                            QuotaExecutionError error => MapExecutionError(error),
                            ManagedSecretEncryptionUnavailable => SeaweedFSHttpMappings.Problem(
                                503,
                                "encryption-unavailable",
                                "Credential encryption unavailable"),
                            ManagedSecretReferenceRejected => SeaweedFSHttpMappings.Problem(
                                409,
                                "secret-reference-rejected",
                                "Credential reference unavailable"),
                            ManagedSecretChanged => SeaweedFSHttpMappings.Problem(
                                409,
                                "secret-changed",
                                "Credential changed concurrently"),
                            PostgresManagedDatabaseUnavailable => SeaweedFSHttpMappings.Problem(
                                422,
                                "metadata-postgres-unavailable",
                                "Metadata Postgres is unavailable for this tenant"),
                            PostgresManagedDatabaseIneligible => SeaweedFSHttpMappings.Problem(
                                422,
                                "metadata-postgres-ineligible",
                                "Metadata Postgres must be synchronous HA on the same deployment target"),
                            PostgresManagedDatabaseChanged => SeaweedFSHttpMappings.Problem(
                                409,
                                "metadata-postgres-changed",
                                "Metadata Postgres changed concurrently"),
                            PostgresManagedDatabaseProtectionUnavailable => SeaweedFSHttpMappings.Problem(
                                503,
                                "metadata-protection-unavailable",
                                "Metadata credential protection unavailable"),
                            ManagedSecretUnavailable => SeaweedFSHttpMappings.Problem(
                                409,
                                "secret-unavailable",
                                "Credential unavailable"),
                        }))
                .WithName("CreateSeaweedFSService")
                .Produces<CreateSeaweedFSServiceResponse>(StatusCodes.Status202Accepted);

            return app;
        }
    }
}

public sealed record SeaweedFSRuntimeClassUnsupported;

public union CreateSeaweedFSServiceOutcome(
    SeaweedFSRuntimeClassUnsupported,
    CreateSeaweedFSServiceResponse,
    SeaweedFSServiceNameAlreadyExists,
    DeploymentTargetNewWorkloadAdmissionError,
    ComputeProfileResolutionError,
    StorageProfileResolutionError,
    CreateResourceDeploymentError,
    ResourceRegistrationMutationError,
    QuotaExecutionError,
    ManagedSecretEncryptionUnavailable,
    ManagedSecretReferenceRejected,
    ManagedSecretChanged,
    ManagedSecretUnavailable,
    PostgresManagedDatabaseUnavailable,
    PostgresManagedDatabaseIneligible,
    PostgresManagedDatabaseChanged,
    PostgresManagedDatabaseProtectionUnavailable);

public sealed class CreateSeaweedFSServiceHandler(
    PersistCreatedSeaweedFS persistCreatedSeaweedFS,
    ResolveStorageProfile resolveStorageProfile,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    AdmitDeploymentTargetForNewWorkload admitDeploymentTarget,
    ResolveComputeProfile resolveComputeProfile,
    CreateResourceDeployment createResourceDeployment,
    IResourceRegistrar resourceRegistrar,
    PrepareManagedSecret prepareManagedSecret,
    CreateBoundManagedSecret createBoundManagedSecret,
    ClaimPostgresManagedDatabase claimPostgresMetadata
) : IMediatorOutcomeHandler<CreateSeaweedFSServiceRequest, CreateSeaweedFSServiceOutcome>
{
    public async Task<CreateSeaweedFSServiceOutcome> HandleAsync(
        CreateSeaweedFSServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var targetResult = await admitDeploymentTarget(
            new DeploymentTargetAdmissionRequest(
                request.TenantId,
                SeaweedFSResourceDeployment.ResourceType,
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
                SeaweedFSResourceDeployment.ResourceType),
            cancellationToken);
        if (computeProfileResult is ComputeProfileResolutionError computeProfileError)
            return computeProfileError;

        var computeProfile = (ResolvedComputeProfile)computeProfileResult.Value;
        if (!string.IsNullOrWhiteSpace(computeProfile.RuntimeClassName))
            return new SeaweedFSRuntimeClassUnsupported();

        var storageProfileResult = await resolveStorageProfile(
            new StorageProfileResolutionRequest(
                request.Body.Spec.Storage.StorageProfileId,
                target.Id,
                request.TenantId,
                SeaweedFSResourceDeployment.ResourceType,
                request.Body.Spec.Storage.SizeGb),
            cancellationToken);
        if (storageProfileResult is StorageProfileResolutionError storageProfileError)
            return storageProfileError;

        var storageProfile = (ResolvedStorageProfile)storageProfileResult.Value;
        var adminSecretId = ObjectId.GenerateNewId().ToString();
        var domainSpec = request.Body.Spec.ToDomain();
        var quotaInput = SeaweedFSQuotaInput.Create(
            domainSpec,
            computeProfile.Requests.CpuMillis,
            computeProfile.Requests.MemoryMi);
        var service = SeaweedFSFactory.Create(
            request.TenantId,
            request.Body.Name,
            request.Body.ResourceGroupId,
            domainSpec,
            request.Body.Tags,
            adminSecretId,
            new(quotaInput.CpuMillis, quotaInput.MemoryMi, quotaInput.Instances, quotaInput.StorageGb));
        var preparedResult = await prepareManagedSecret(
            SeaweedFSCredentials.Prepare(
                request.TenantId,
                adminSecretId,
                SeaweedFSCredentials.AdminOwner(service.Id),
                1,
                ManagedSecretSharingPolicy.OwnerOnly),
            cancellationToken);
        if (preparedResult is ManagedSecretEncryptionUnavailable encryptionError)
            return encryptionError;
        var prepared = (PreparedManagedSecret)preparedResult.Value;
        var deploymentId = ObjectId.GenerateNewId().ToString();
        var resolvedStorage = new ResourceDeploymentStorageConfiguration(
            storageProfile.ProfileId,
            storageProfile.ProfileRevision,
            storageProfile.StorageClassName);

        return await quotaAdmissionExecutor.ExecuteOutcomeAsync<SeaweedFSQuotaInput, CreateSeaweedFSServiceOutcome>(
            new ExecuteQuotaAdmissionRequest<SeaweedFSQuotaInput, CreateSeaweedFSServiceOutcome>(
                TenantId: request.TenantId,
                ResourceKey: SeaweedFSQuotaResourceKey.Create(service.Id),
                Mutation: new CreateQuotaMutation<SeaweedFSQuotaInput>(quotaInput),
                QuotaError: quotaError => quotaError,
                MutationCallback: async (transaction, transactionCancellationToken) =>
                {
                    var registration = await resourceRegistrar.RegisterAsync(
                        new RegisterResourceRequest(
                            TenantId: service.TenantId,
                            ResourceGroupId: service.ResourceGroupId,
                            Resource: new ResourceRegistrationDescriptor(
                                SeaweedFSResourceDeployment.ResourceType,
                                service.Id,
                                service.Name)),
                        session: transaction.Session,
                        cancellationToken: transactionCancellationToken);
                    if (registration is ResourceRegistrationMutationError registrationError)
                        return transaction.Abort(registrationError);

                    var deploymentResult = await createResourceDeployment(
                        new CreateResourceDeploymentRequest(
                            deploymentId,
                            request.TenantId,
                            SeaweedFSResourceDeployment.ResourceType,
                            service.Id,
                            service.ResourceGroupId,
                            request.Body.Placement?.IsolationScopeId,
                            ToResolvedResourceDeploymentTarget(target),
                            computeProfile.ToResourceDeploymentConfiguration(),
                            resolvedStorage,
                            ResourcePlacementCapability.SingleTarget,
                            FixedReplicas: service.Spec.Mode == SeaweedFSMode.Replicated ? 3 : 1),
                        transaction.Session,
                        transactionCancellationToken);
                    if (deploymentResult is CreateResourceDeploymentError deploymentError)
                        return transaction.Abort(deploymentError);
                    var deployment = (PlacementSummaryDto)deploymentResult.Value;

                    if (service.Spec.Metadata is ExistingPostgresSeaweedFSMetadata existingMetadata)
                    {
                        var claimed = await claimPostgresMetadata(
                            new(
                                request.TenantId,
                                existingMetadata.PostgresId,
                                new PostgresManagedDatabaseOwner("pole/seaweedfs", service.Id),
                                new PostgresManagedDatabasePlacement(
                                    service.ResourceGroupId,
                                    deployment.IsolationScopeId,
                                    deployment.DeploymentTargetId),
                                "filer"),
                            transaction.Session,
                            transactionCancellationToken);
                        if (claimed is PostgresManagedDatabaseUnavailable unavailable)
                            return transaction.Abort(unavailable);
                        if (claimed is PostgresManagedDatabaseIneligible ineligible)
                            return transaction.Abort(ineligible);
                        if (claimed is PostgresManagedDatabaseChanged changed)
                            return transaction.Abort(changed);
                        if (claimed is PostgresManagedDatabaseProtectionUnavailable protection)
                            return transaction.Abort(protection);
                        var reference = (PostgresManagedDatabaseReference)claimed.Value;
                        if (reference.PostgresClusterId != existingMetadata.PostgresId)
                            return transaction.Abort(new PostgresManagedDatabaseChanged());
                    }

                    var persisted = await persistCreatedSeaweedFS(
                        transaction.Session,
                        service,
                        transactionCancellationToken);
                    if (persisted is SeaweedFSServiceNameAlreadyExists nameConflict)
                        return transaction.Abort(new CreateSeaweedFSServiceOutcome(nameConflict));

                    var secretResult = await createBoundManagedSecret(
                        new CreateBoundManagedSecretRequest(
                            prepared,
                            service.ResourceGroupId,
                            deployment.IsolationScopeId,
                            deployment.DeploymentId),
                        transaction.Session,
                        transactionCancellationToken);
                    if (secretResult is ManagedSecretEncryptionUnavailable encryptionFailure)
                        return transaction.Abort(encryptionFailure);
                    if (secretResult is ManagedSecretReferenceRejected referenceFailure)
                        return transaction.Abort(referenceFailure);
                    if (secretResult is ManagedSecretChanged changedFailure)
                        return transaction.Abort(changedFailure);
                    if (secretResult is ManagedSecretUnavailable unavailableFailure)
                        return transaction.Abort(unavailableFailure);
                    return transaction.Commit(
                        new CreateSeaweedFSServiceOutcome(new CreateSeaweedFSServiceResponse(service.Id, deployment)));
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

public sealed record CreateSeaweedFSServiceRequest(
    [FromBody] CreateSeaweedFSServiceRequestBody Body,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<CreateSeaweedFSServiceOutcome>, ITenantCreateOperation;

public sealed record CreateSeaweedFSServiceRequestBody(
    string Name,
    string ResourceGroupId,
    SeaweedFSSpecDto Spec,
    ImmutableHashSet<string> Tags,
    string? ComputeProfileId = null,
    PlacementRequest? Placement = null
);

public sealed record CreateSeaweedFSServiceResponse(
    string SeaweedFSId,
    PlacementSummaryDto Placement
);

public sealed class CreateSeaweedFSServiceRequestValidator : AbstractValidator<CreateSeaweedFSServiceRequest>
{
    public CreateSeaweedFSServiceRequestValidator()
    {
        RuleFor(x => x.Body).NotNull().SetValidator(new CreateSeaweedFSServiceRequestBodyValidator());
    }
}

public sealed class CreateSeaweedFSServiceRequestBodyValidator : AbstractValidator<CreateSeaweedFSServiceRequestBody>
{
    public CreateSeaweedFSServiceRequestBodyValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(30)
            .Matches("^[a-z0-9-]+$")
            .WithMessage("Name must contain only lowercase letters, numbers, and hyphens.");
        RuleFor(x => x.ResourceGroupId).NotEmpty().MongoObjectId("Resource group ID");
        RuleFor(x => x.Spec)
            .NotNull()
            .DependentRules(() =>
            {
                RuleFor(x => x.Spec.Mode).IsInEnum();
                RuleFor(x => x.Spec.ExistingPostgresId).OptionalMongoObjectId("Postgres cluster ID");
                RuleFor(x => x.Spec.ExistingPostgresId)
                    .Must((body, id) => id is null || body.Spec.Mode == SeaweedFSMode.Replicated)
                    .WithMessage("Existing Postgres metadata requires Replicated mode.");
                RuleFor(x => x.Spec.Storage)
                    .NotNull()
                    .DependentRules(() =>
                    {
                        RuleFor(x => x.Spec.Storage.SizeGb).InclusiveBetween(1, SeaweedFSStorage.MaximumSizeGb);
                        RuleFor(x => x.Spec.Storage.StorageProfileId).MongoObjectId("Storage profile ID");
                    });
            });
        RuleFor(x => x.Tags).NotNull();
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
