using MongoDB.Driver;
using Pole.Features.StorageProfiles.Primitives;
using Pole.Workloads;

namespace Pole.Features.StorageProfiles.Contracts;

public sealed record ResolvedStorageProfile(
    string ProfileId,
    long ProfileRevision,
    string DeploymentTargetId,
    string DisplayName,
    string StorageClassName,
    StorageMediaType MediaType,
    StoragePerformanceTier PerformanceTier
);

public sealed record ResolvedStorageProfileDto(
    string ProfileId,
    long ProfileRevision,
    string DeploymentTargetId,
    string DisplayName,
    string StorageClassName,
    string MediaType,
    string PerformanceTier
);

public static class ResolvedStorageProfileExtensions
{
    public static ResolvedStorageProfileDto ToDto(this ResolvedStorageProfile profile) =>
        new(
            profile.ProfileId,
            profile.ProfileRevision,
            profile.DeploymentTargetId,
            profile.DisplayName,
            profile.StorageClassName,
            profile.MediaType.ToString(),
            profile.PerformanceTier.ToString());
}

public sealed record StorageProfileResolutionRequest(
    string StorageProfileId,
    string DeploymentTargetId,
    string TenantId,
    ResourceTypeId ResourceType,
    int SizeGb,
    long? Revision = null
);

public sealed record StorageProfileNotFound(string StorageProfileId);

public sealed record StorageProfileRevisionUnavailable(
    string StorageProfileId,
    long Revision);

public sealed record StorageProfileDisabled(string StorageProfileId);

public sealed record StorageProfileTenantAccessDenied(
    string StorageProfileId,
    string TenantId);

public sealed record StorageProfileResourceTypeNotAllowed(
    string StorageProfileId,
    ResourceTypeId ResourceType);

public sealed record StorageProfileSizeNotAllowed(
    string StorageProfileId,
    int SizeGb);

public sealed record StorageProfileDeploymentTargetNotConfigured(
    string StorageProfileId,
    string DeploymentTargetId);

public sealed record StorageProfileStorageClassUnavailable(
    string StorageProfileId,
    string DeploymentTargetId,
    string StorageClassName);

/// <summary>
/// The configured deployment target cannot currently be queried for storage
/// classes. Provider details are deliberately not exposed at this boundary.
/// </summary>
public sealed record StorageProfileDiscoveryUnavailable(string DeploymentTargetId);

public union StorageProfileResolutionError(
    StorageProfileNotFound,
    StorageProfileRevisionUnavailable,
    StorageProfileDisabled,
    StorageProfileTenantAccessDenied,
    StorageProfileResourceTypeNotAllowed,
    StorageProfileSizeNotAllowed,
    StorageProfileDeploymentTargetNotConfigured,
    StorageProfileStorageClassUnavailable,
    StorageProfileDiscoveryUnavailable);

/// <summary>
/// Resolves a storage profile into the immutable storage-class snapshot used by
/// a workload deployment. Structural request violations throw; expected
/// availability and policy decisions are represented by the closed error union.
/// </summary>
public delegate Task<ResolveStorageProfileOutcome>
    ResolveStorageProfile(
        StorageProfileResolutionRequest request,
        CancellationToken cancellationToken = default);

public sealed record StorageProfileResolutionFenceRequest(
    string StorageProfileId,
    long StorageProfileRevision,
    string DeploymentTargetId,
    string StorageClassName,
    long? PolicyRevision = null);

public sealed record StorageProfileResolutionFenceRejected(string StorageProfileId);

public delegate Task<FenceStorageProfileResolutionOutcome>
    FenceStorageProfileResolution(
        StorageProfileResolutionFenceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

public static class PoleStorageLabels
{
    public const string TenantId = "pole.io/tenant-id";
    public const string ResourceGroupId = "pole.io/resource-group-id";
    public const string ResourceType = "pole.io/resource-type";
    public const string ResourceId = "pole.io/resource-id";
    public const string ResourceName = "pole.io/resource-name";
}

public union ResolveStorageProfileOutcome(
    ResolvedStorageProfile,
    StorageProfileResolutionError);

public sealed record StorageProfileResolutionFenced;

public union FenceStorageProfileResolutionOutcome(
    StorageProfileResolutionFenced,
    StorageProfileResolutionFenceRejected);
