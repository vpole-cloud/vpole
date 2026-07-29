using Pole.Features.Shared.Models;
using Pole.ResultType;

namespace Pole.Features.StorageProfiles.Contracts;

public enum StorageMediaType
{
    Unknown,
    HDD,
    SSD,
    NVMe,
    Network
}

public enum StoragePerformanceTier
{
    Standard,
    Bulk,
    Fast,
    Ultra
}

public enum StorageProfileTenantAccessMode
{
    AllTenants,
    SelectedTenants
}

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
    ResourceType ResourceType,
    int SizeGb
);

public interface IStorageProfileResolver
{
    Task<Result<ResolvedStorageProfile>> ResolveAsync(
        StorageProfileResolutionRequest request,
        CancellationToken cancellationToken = default);
}

public static class PoleStorageLabels
{
    public const string TenantId = "pole.io/tenant-id";
    public const string ResourceGroupId = "pole.io/resource-group-id";
    public const string ResourceType = "pole.io/resource-type";
    public const string ResourceId = "pole.io/resource-id";
    public const string ResourceName = "pole.io/resource-name";
}
