// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.StorageProfiles.Contracts;

namespace Pole.Features.StorageProfiles.Application.Observation;

public sealed record StorageObservationDemandIdentity(
    string DeploymentTargetId,
    string SubscriptionId,
    string ConnectionId,
    string SessionId
);

public sealed record StorageObservationDemand(
    StorageObservationDemandIdentity Identity,
    DateTimeOffset ExpiresAtUtc
);

public sealed record StorageObservationLease(
    string DeploymentTargetId,
    string InstanceId,
    string Token,
    long Epoch
);

public sealed record StorageObservationOwnership(
    StorageObservationLease Lease,
    long SourceControlVersion
);

public sealed record StorageObservationSourceState(
    long SourceControlVersion
);

public sealed record StorageObservationMutationAccepted;

public enum StorageObservationRejection
{
    Busy,
    NoDemand,
    LeaseLost,
    Superseded,
    SourceChanged,
    TargetUnavailable,
    InventoryIncomplete,
}

public sealed record StorageObservationRejected(
    StorageObservationRejection Reason
);

public union StorageObservationMutationOutcome(StorageObservationMutationAccepted, StorageObservationRejected);

public union StorageObservationLeaseOutcome(StorageObservationOwnership, StorageObservationRejected);

public union StorageObservationSourceOutcome(StorageObservationSourceState, StorageObservationRejected);

public union StorageObservationSnapshotOutcome(StorageObservationSnapshot, StorageObservationRejected);

public sealed record StorageObservationControlState(
    long ConnectionVersion,
    long Epoch,
    long SourceControlVersion,
    long ObservationVersion,
    bool SourceHealthy,
    string? SourceId,
    DateTime? LeaseExpiresAtUtc
);

public sealed record StorageObservationDemandPage(
    IReadOnlyList<string> DeploymentTargetIds,
    string? NextCursor
);

public interface IStorageObservationStore
{
    Task<StorageObservationMutationOutcome> PutDemandAsync(
        StorageObservationDemand demand,
        StorageObservationContext context,
        CancellationToken ct = default);

    Task<StorageObservationMutationOutcome> RenewDemandAsync(
        StorageObservationDemand demand,
        StorageObservationContext context,
        CancellationToken ct = default);

    Task<StorageObservationMutationOutcome> RemoveDemandAsync(
        StorageObservationDemandIdentity identity,
        CancellationToken ct = default);

    Task<StorageObservationDemandPage> ScanDemandsAsync(
        string? after,
        int limit,
        CancellationToken ct = default);

    Task ReapExpiredDemandsAsync(int limit, CancellationToken ct = default);

    Task<StorageObservationLeaseOutcome> ClaimLeaseAsync(
        StorageObservationContext context,
        string instanceId,
        CancellationToken ct = default);

    Task<StorageObservationMutationOutcome> RenewLeaseAsync(
        StorageObservationLease lease,
        StorageObservationContext context,
        CancellationToken ct = default);

    Task<StorageObservationMutationOutcome> ReleaseLeaseAsync(
        StorageObservationLease lease,
        CancellationToken ct = default);

    Task<StorageObservationSourceOutcome> SetSourceStateAsync(
        StorageObservationLease lease,
        StorageObservationContext context,
        long expectedSourceVersion,
        bool healthy,
        string? sourceId,
        CancellationToken ct = default);

    Task<StorageObservationSnapshotOutcome> TryCommitSnapshotAsync(
        StorageObservationLease lease,
        StorageObservationContext context,
        long sourceVersion,
        string sourceId,
        StorageInventoryObservation inventory,
        CancellationToken ct = default);

    Task<StorageObservationControlState?> LoadControlAsync(string targetId, CancellationToken ct = default);

    Task<StorageObservationSnapshot?> LoadFreshSnapshotAsync(
        StorageObservationContext context,
        CancellationToken ct = default);
}
