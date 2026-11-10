using Pole.Workloads;

namespace Pole.Features.StorageProfiles.Contracts;

public sealed record NamedStorageProfileRequest(
    string TenantId,
    ResourceTypeId ResourceType,
    string Name,
    string DeploymentTargetId,
    int SizeGb,
    long Revision
);

public sealed record NamedStorageProfileUnavailable(
    string Name
);

public union ResolveNamedStorageProfileOutcome(
    ResolvedStorageProfile,
    NamedStorageProfileUnavailable,
    StorageProfileResolutionError);

public delegate Task<ResolveNamedStorageProfileOutcome> ResolveNamedStorageProfile(
    NamedStorageProfileRequest request,
    CancellationToken cancellationToken);
