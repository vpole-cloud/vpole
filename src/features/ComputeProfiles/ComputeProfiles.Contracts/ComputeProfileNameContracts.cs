using Pole.Workloads;

namespace Pole.Features.ComputeProfiles.Contracts;

public sealed record NamedComputeProfileRequest(
    string TenantId,
    ResourceTypeId ResourceType,
    string Name,
    string DeploymentTargetId,
    long Revision);

public sealed record NamedComputeProfileUnavailable(string Name);

public sealed record NamedComputeProfileAmbiguous(string Name);

public union ResolveNamedComputeProfileOutcome(
    ResolvedComputeProfile,
    NamedComputeProfileUnavailable,
    NamedComputeProfileAmbiguous,
    ComputeProfileUnavailable);

public delegate Task<ResolveNamedComputeProfileOutcome> ResolveNamedComputeProfile(
    NamedComputeProfileRequest request,
    CancellationToken cancellationToken = default);
