// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.StorageProfiles.Contracts;

public sealed record StorageObservationContext(
    string DeploymentTargetId,
    long ConnectionVersion
);

public sealed record StorageClassObservation(
    string Name,
    string Provisioner,
    bool IsDefault,
    string? ReclaimPolicy,
    string? VolumeBindingMode,
    bool? AllowVolumeExpansion
);

public sealed record StorageOwnerObservation(
    string? TenantId,
    string? ResourceGroupId,
    string? ResourceType,
    string? ResourceId,
    string? ResourceName
);

public sealed record PersistentVolumeObservation(
    string Name,
    string? Capacity,
    string? Status,
    string? StorageClassName,
    string? BoundClaimNamespace,
    string? BoundClaimName,
    string? ReclaimPolicy,
    StorageOwnerObservation Owner
);

public sealed record PersistentVolumeClaimObservation(
    string Name,
    string Namespace,
    string? Capacity,
    string? RequestedCapacity,
    string? Status,
    string? StorageClassName,
    string? VolumeName,
    StorageOwnerObservation Owner
);

/// <summary>One bounded aggregate cut. Native specs, arbitrary metadata and parameters are excluded.</summary>
public sealed record StorageInventoryObservation(
    StorageClassObservation[] StorageClasses,
    PersistentVolumeObservation[] PersistentVolumes,
    PersistentVolumeClaimObservation[] PersistentVolumeClaims,
    bool Complete
);

public sealed record StorageObservationSnapshot(
    string DeploymentTargetId,
    long ConnectionVersion,
    long? ObservationVersion,
    DateTime CheckedAtUtc,
    bool Complete,
    StorageClassObservation[] StorageClasses,
    PersistentVolumeObservation[] PersistentVolumes,
    PersistentVolumeClaimObservation[] PersistentVolumeClaims
);

public enum StorageObservationChangeKind
{
    Ready,
    Changed,
    Unavailable,
}

public sealed record StorageObservationChange(
    StorageObservationChangeKind Kind,
    string? SourceId = null,
    StorageInventoryObservation? Inventory = null
);

/// <summary>The storage owner supplies sanitized three-stream cuts and coverage transitions.</summary>
public interface IStorageObservationSource
{
    IAsyncEnumerable<StorageObservationChange> WatchAsync(
        StorageObservationContext context,
        CancellationToken cancellationToken = default);
}
