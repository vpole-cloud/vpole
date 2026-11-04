using System.Text.Json;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Quotas.Contracts;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Persistence.Mutations;
using Pole.Features.Functions.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.Functions.Application.Persistence.Lifecycle;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Contracts;

namespace Pole.Features.Functions.Application.Persistence;

public enum FunctionDeclarationError
{
    AcceptedSnapshotRequired,
    ResourceDeleting,
    ResourceGroupUnavailable,
    IsolationScopeUnavailable,
    DeploymentTargetUnavailable,
    ComputeProfileUnavailable,
    PlacementUnavailable,
    ResourceRegistrationUnavailable,
    QuotaUnavailable,
    ExplicitAdoptionRequired,
    UnexpectedOperation,
    FunctionRouteUnavailable,
    ConfigurationUnavailable,
    PendingRevision,
    SecretReferenceUnavailable
}

public sealed record FunctionDeclarationRejected(FunctionDeclarationError Reason) : IDeclarationFailure;

public sealed record FunctionDeclarationSecretRejected(ConsumerSecretReferenceError Reason) : IDeclarationFailure;

internal sealed partial class DeclarativeFunctionProvider(
    IMongoCollection<FunctionApp> resources,
    IMongoCollection<FunctionRevision> revisions,
    IMongoCollection<FunctionRouteBinding> routes,
    IGitOpsMutations git,
    MongoRunTransaction transactions,
    LoadIsolationScopePlanningSnapshot loadScope,
    FenceIsolationScopePlanningSnapshot fenceScope,
    ResolveNamedDeploymentTarget resolveTargetName,
    ResolveNamedComputeProfile resolveComputeName,
    ResolveDeploymentTargetPlanningSnapshot loadTarget,
    FenceDeploymentTargetPlanningSnapshot fenceTarget,
    ResolveComputeProfilePlanningSnapshot loadCompute,
    FenceComputeProfilePlanningSnapshot fenceCompute,
    LoadResourceDeploymentPlanningSnapshot loadPlacements,
    FenceResourceDeploymentPlanningSnapshot fencePlacements,
    IQuotaAdmissionExecutor quotas,
    IResourceRegistrar registrar,
    CreateResourceDeployment createPlacement,
    RequestResourceDeploymentSetApply applyPlacements,
    LoadConsumerSecretPlanningSnapshot loadSecrets,
    FenceConsumerSecretPlanningSnapshot fenceSecrets,
    AdmitConsumerSecretReferences admitSecrets,
    PersistFunctionAppRevision persistRevision,
    TimeProvider clock,
    IQuotaUsageRelease releaseQuota,
    IGitLifecyclePlanGuard lifecycleGuard,
    LoadResourceConnectionCascadePlanning loadCascadePlanning,
    ApplyFunctionAppDeletion deleteWorkflow) : ITransactionalDeclarativeResourceProvider,
    IDeclarativeReferencePreparation,
    IDeclarativeRegisteredResourceProvider,
    IDeclarativeResourceLifecycleProvider,
    IDeclarativeLifecycleDependencyProvider,
    IDeclarativePreviewReferenceProvider,
    IDeclarativeAdoptionPreviewProvider
{
    public const string ResourceKind = "FunctionApp";
    public string Kind => ResourceKind;
    public int MaximumNameLength => 63;

    public string DescribeFailure(IDeclarationFailure failure) => failure switch
    {
        FunctionLifecycleDeploymentRejected => nameof(FunctionLifecycleDeploymentRejected),
        FunctionDeclarationSecretRejected rejected => ConsumerSecretReferenceErrorCodes.Code(rejected.Reason),
        FunctionDeclarationRejected rejected => rejected.Reason switch
        {
            FunctionDeclarationError.AcceptedSnapshotRequired =>
                nameof(FunctionDeclarationError.AcceptedSnapshotRequired),
            FunctionDeclarationError.ResourceDeleting => nameof(FunctionDeclarationError.ResourceDeleting),
            FunctionDeclarationError.ResourceGroupUnavailable =>
                nameof(FunctionDeclarationError.ResourceGroupUnavailable),
            FunctionDeclarationError.IsolationScopeUnavailable =>
                nameof(FunctionDeclarationError.IsolationScopeUnavailable),
            FunctionDeclarationError.DeploymentTargetUnavailable =>
                nameof(FunctionDeclarationError.DeploymentTargetUnavailable),
            FunctionDeclarationError.ComputeProfileUnavailable =>
                nameof(FunctionDeclarationError.ComputeProfileUnavailable),
            FunctionDeclarationError.PlacementUnavailable => nameof(FunctionDeclarationError.PlacementUnavailable),
            FunctionDeclarationError.ResourceRegistrationUnavailable =>
                nameof(FunctionDeclarationError.ResourceRegistrationUnavailable),
            FunctionDeclarationError.QuotaUnavailable => nameof(FunctionDeclarationError.QuotaUnavailable),
            FunctionDeclarationError.ExplicitAdoptionRequired =>
                nameof(FunctionDeclarationError.ExplicitAdoptionRequired),
            FunctionDeclarationError.UnexpectedOperation => nameof(FunctionDeclarationError.UnexpectedOperation),
            FunctionDeclarationError.FunctionRouteUnavailable =>
                nameof(FunctionDeclarationError.FunctionRouteUnavailable),
            FunctionDeclarationError.ConfigurationUnavailable =>
                nameof(FunctionDeclarationError.ConfigurationUnavailable),
            FunctionDeclarationError.PendingRevision => nameof(FunctionDeclarationError.PendingRevision),
            FunctionDeclarationError.SecretReferenceUnavailable =>
                nameof(FunctionDeclarationError.SecretReferenceUnavailable),
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        },
        _ => throw new ArgumentException("The failure belongs to another declaration provider.", nameof(failure))
    };

    public JsonElement NormalizeAndValidate(JsonElement spec) => FunctionDeclarationJson.Normalize(spec);

    public DeclarationFacts Facts(JsonElement spec)
    {
        var value = FunctionDeclarationJson.Read(spec);
        return new(new("ResourceGroup", value.ResourceGroup), [],
        [
            .. value.Placement.IsolationScope is { } scope
                ? new DeclaredReference[] { new(new("IsolationScope", scope), "spec.placement.isolationScope") } : [],
            .. value.SecretEnvironment.Select(pair => new DeclaredReference(new("Secret", pair.Value.Secret),
                $"spec.secretEnvironment.{pair.Key}", pair.Value.Key, RequiresAccess: true))
        ], [], []);
    }

    public IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec)
    {
        DeclarationJson.Require(address.Name.Length >= 3, "metadata.name");
        var value = FunctionDeclarationJson.Read(spec);
        return
        [
            new(DeclarationPhase.Prepare,
            [
                new(new("ResourceGroup", value.ResourceGroup), DeclarationPhase.Prepare),
                .. value.Placement.IsolationScope is { } scope
                    ? new DeclarationDependency[] { new(new("IsolationScope", scope), DeclarationPhase.Prepare) } : []
            ]),
            new(DeclarationPhase.Configure,
            [
                new(address, DeclarationPhase.Prepare),
                .. value.SecretEnvironment.Values.Select(reference => new DeclarationDependency(
                    new("Secret", reference.Secret), DeclarationPhase.Prepare))
            ])
        ];
    }

    public async Task<string?> ResolveAsync(
        ResolveDeclaredResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct) =>
        (await resources.Find(session, FunctionAppFilters.ByName(request.TenantId, request.Name))
            .FirstOrDefaultAsync(ct))?.Id;

    private static DeclarationReadBlocked Blocked(FunctionDeclarationError reason) =>
        new(new FunctionDeclarationRejected(reason));
}
