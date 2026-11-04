using System.Text.Json;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Quotas.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Persistence.Mutations;
using Pole.Features.RedisServices.Application.Persistence.Lifecycle;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Workloads;

namespace Pole.Features.RedisServices.Application.Persistence;

public sealed record RedisPinnedProfile(
    string Name,
    long Revision);

public sealed record RedisDeclarationPlacement(
    string DeploymentTarget,
    RedisPinnedProfile ComputeProfile,
    string? IsolationScope);

public sealed record RedisDeclarationStorage(
    int SizeGb,
    RedisPinnedProfile Profile);

public sealed record RedisDeclaration
{
    public required string ResourceGroup { get; init; }
    public required RedisDeclarationPlacement Placement { get; init; }
    public required RedisDeclarationStorage Storage { get; init; }
    public string[] Tags { get; init; } = [];
}

public enum RedisDeclarationError
{
    AcceptedSnapshotRequired,
    ResourceDeleting,
    ResourceGroupUnavailable,
    IsolationScopeUnavailable,
    DeploymentTargetUnavailable,
    ComputeProfileUnavailable,
    StorageProfileUnavailable,
    PlacementUnavailable,
    ResourceRegistrationUnavailable,
    QuotaUnavailable,
    ExplicitAdoptionRequired,
    UnexpectedOperation
}

public sealed record RedisDeclarationRejected(RedisDeclarationError Reason) : IDeclarationFailure;

internal sealed partial class DeclarativeRedisProvider(
    IMongoCollection<Redis> resources,
    IGitOpsMutations git,
    MongoRunTransaction transactions,
    LoadIsolationScopePlanningSnapshot loadScope,
    FenceIsolationScopePlanningSnapshot fenceScope,
    ResolveNamedDeploymentTarget resolveTargetName,
    ResolveNamedComputeProfile resolveComputeName,
    ResolveNamedStorageProfile resolveStorageName,
    ResolveDeploymentTargetPlanningSnapshot loadTarget,
    FenceDeploymentTargetPlanningSnapshot fenceTarget,
    ResolveComputeProfilePlanningSnapshot loadCompute,
    FenceComputeProfilePlanningSnapshot fenceCompute,
    ResolveStorageProfilePlanningSnapshot loadStorage,
    FenceStorageProfilePlanningSnapshot fenceStorage,
    LoadResourceDeploymentPlanningSnapshot loadPlacements,
    FenceResourceDeploymentPlanningSnapshot fencePlacements,
    IQuotaAdmissionExecutor quotas,
    IResourceRegistrar registrar,
    CreateResourceDeployment createPlacement,
    RequestResourceDeploymentSetApply applyPlacements,
    PersistCreatedRedis persistCreated,
    PersistUpdatedRedis persistUpdated,
    BeginRedisDeletion beginDeletion,
    RequestResourceDeploymentDeletion deletePlacement,
    IQuotaUsageRelease releaseQuota,
    IGitLifecyclePlanGuard lifecycleGuard,
    LoadResourceConnectionCascadePlanning loadCascade,
    TimeProvider clock) : ITransactionalDeclarativeResourceProvider, IDeclarativeReferencePreparation,
    IDeclarativeResourceLifecycleProvider, IDeclarativeLifecycleDependencyProvider,
    IDeclarativeRegisteredResourceProvider, IDeclarativeAdoptionPreviewProvider,
    IDeclarativePreviewReferenceProvider, IDeclarativeLifecycleWarningProvider
{
    public const string ResourceKind = "RedisService";
    public string Kind => ResourceKind;
    public ResourceTypeId ResourceType => RedisResourceDeployment.ResourceType;
    public int MaximumNameLength => 30;

    public string DescribeFailure(IDeclarationFailure failure) => failure switch
    {
        RedisDeclarationRejected rejected => rejected.Reason switch
        {
            RedisDeclarationError.AcceptedSnapshotRequired => nameof(RedisDeclarationError.AcceptedSnapshotRequired),
            RedisDeclarationError.ResourceDeleting => nameof(RedisDeclarationError.ResourceDeleting),
            RedisDeclarationError.ResourceGroupUnavailable => nameof(RedisDeclarationError.ResourceGroupUnavailable),
            RedisDeclarationError.IsolationScopeUnavailable => nameof(RedisDeclarationError.IsolationScopeUnavailable),
            RedisDeclarationError.DeploymentTargetUnavailable =>
                nameof(RedisDeclarationError.DeploymentTargetUnavailable),
            RedisDeclarationError.ComputeProfileUnavailable => nameof(RedisDeclarationError.ComputeProfileUnavailable),
            RedisDeclarationError.StorageProfileUnavailable => nameof(RedisDeclarationError.StorageProfileUnavailable),
            RedisDeclarationError.PlacementUnavailable => nameof(RedisDeclarationError.PlacementUnavailable),
            RedisDeclarationError.ResourceRegistrationUnavailable =>
                nameof(RedisDeclarationError.ResourceRegistrationUnavailable),
            RedisDeclarationError.QuotaUnavailable => nameof(RedisDeclarationError.QuotaUnavailable),
            RedisDeclarationError.ExplicitAdoptionRequired => nameof(RedisDeclarationError.ExplicitAdoptionRequired),
            RedisDeclarationError.UnexpectedOperation => nameof(RedisDeclarationError.UnexpectedOperation),
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        },
        _ => throw new ArgumentException("The failure belongs to another declaration provider.", nameof(failure))
    };

    public JsonElement NormalizeAndValidate(JsonElement spec)
    {
        var value = DeclarationJson.Read<RedisDeclaration>(spec);
        DeclarationJson.Require(DeclarationNames.Valid(value.ResourceGroup), "spec.resourceGroup");
        DeclarationJson.Require(value.Placement is not null, "spec.placement");
        DeclarationJson.Require(DeclarationNames.Valid(value.Placement.DeploymentTarget),
            "spec.placement.deploymentTarget");
        DeclarationJson.Require(value.Placement.IsolationScope is null ||
            DeclarationNames.Valid(value.Placement.IsolationScope) && value.Placement.IsolationScope != "default",
            "spec.placement.isolationScope");
        DeclarationJson.Require(ValidProfile(value.Placement.ComputeProfile), "spec.placement.computeProfile");
        DeclarationJson.Require(value.Storage is not null && value.Storage.SizeGb is >= 1 and <= 1000,
            "spec.storage.sizeGb");
        DeclarationJson.Require(ValidProfile(value.Storage.Profile), "spec.storage.profile");
        DeclarationJson.Require(value.Tags is not null && value.Tags.All(tag => !string.IsNullOrWhiteSpace(tag)),
            "spec.tags");
        return DeclarationJson.Write(value with
        { Tags = value.Tags.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() });
    }

    public DeclarationFacts Facts(JsonElement spec)
    {
        var value = DeclarationJson.Read<RedisDeclaration>(spec);
        return new(new("ResourceGroup", value.ResourceGroup), [],
            value.Placement.IsolationScope is { } scope
                ? [new(new("IsolationScope", scope), "spec.placement.isolationScope")]
                : [], [], []);
    }

    public IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec)
    {
        var value = DeclarationJson.Read<RedisDeclaration>(spec);
        return
        [
            new(DeclarationPhase.Prepare,
            [
                new(new("ResourceGroup", value.ResourceGroup), DeclarationPhase.Prepare),
                .. value.Placement.IsolationScope is { } scope
                    ? new DeclarationDependency[] { new(new("IsolationScope", scope), DeclarationPhase.Prepare) }
                    : []
            ]),
            new(DeclarationPhase.Configure, [new(address, DeclarationPhase.Prepare)])
        ];
    }

    public async Task<string?> ResolveAsync(
        ResolveDeclaredResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct) =>
        (await resources.Find(session, RedisFilters.ByName(request.TenantId, request.Name))
            .FirstOrDefaultAsync(ct))?.Id;

    private static bool ValidProfile(RedisPinnedProfile? profile) =>
        profile is not null && DeclarationNames.Valid(profile.Name) && profile.Revision > 0;

    private static DeclarationReadBlocked Blocked(RedisDeclarationError reason) =>
        new(new RedisDeclarationRejected(reason));
}
