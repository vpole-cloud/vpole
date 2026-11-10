namespace Pole.Features.StorageProfiles.Domain.Observation;

public sealed record StoredStorageClass(
    string Name,
    string Provisioner,
    bool IsDefault,
    string? ReclaimPolicy,
    string? VolumeBindingMode,
    bool? AllowVolumeExpansion
);

public sealed record StoredStorageOwner(
    string? TenantId,
    string? ResourceGroupId,
    string? ResourceType,
    string? ResourceId,
    string? ResourceName
);

public sealed record StoredPersistentVolume(
    string Name,
    string? Capacity,
    StoredVolumeStatus? Status,
    string? StorageClassName,
    string? BoundClaimNamespace,
    string? BoundClaimName,
    string? ReclaimPolicy,
    StoredStorageOwner Owner
);

public sealed record StoredPersistentVolumeClaim(
    string Name,
    string Namespace,
    string? Capacity,
    string? RequestedCapacity,
    StoredClaimStatus? Status,
    string? StorageClassName,
    string? VolumeName,
    StoredStorageOwner Owner
);

public sealed record StoredStorageInventory(
    StoredStorageClass[] StorageClasses,
    StoredPersistentVolume[] PersistentVolumes,
    StoredPersistentVolumeClaim[] PersistentVolumeClaims,
    bool Complete
);

public enum StoredVolumeStatus
{
    Pending,
    Available,
    Bound,
    Released,
    Failed,
}

public enum StoredClaimStatus
{
    Pending,
    Bound,
    Lost,
}
