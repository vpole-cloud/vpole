using MongoDB.Driver;
using Pole.Workloads;

namespace Pole.Features.StorageProfiles.Contracts;

public sealed record StorageProfilePlanningRequest(
    string TenantId,
    ResourceTypeId ResourceType,
    string StorageProfileId,
    string DeploymentTargetId,
    int SizeGb,
    long Revision);

public sealed record StorageProfilePlanningSnapshot(
    StorageProfilePlanningRequest Request,
    long PolicyRevision,
    ResolvedStorageProfile Storage);

public sealed record StorageProfilePlanningUnavailable(StorageProfileResolutionError Error);

public sealed record StorageProfilePlanningFenced;

public sealed record StorageProfilePlanningFenceRejected;

public union ResolveStorageProfilePlanningSnapshotOutcome(
    StorageProfilePlanningSnapshot,
    StorageProfilePlanningUnavailable);

public union FenceStorageProfilePlanningSnapshotOutcome(
    StorageProfilePlanningFenced,
    StorageProfilePlanningFenceRejected);

public delegate Task<ResolveStorageProfilePlanningSnapshotOutcome> ResolveStorageProfilePlanningSnapshot(
    StorageProfilePlanningRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);

/// <summary>
/// Takes real write fences without changing business revisions. A rejected guard requires
/// the caller to abort the entire transaction, including any preceding guards.
/// </summary>
public delegate Task<FenceStorageProfilePlanningSnapshotOutcome> FenceStorageProfilePlanningSnapshot(
    StorageProfilePlanningSnapshot request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);
