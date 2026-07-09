namespace Pole.Features.StorageProfiles.Application.Common;

public sealed record KubernetesStorageClassDto(
    string Name,
    string Provisioner,
    bool IsDefault,
    string? ReclaimPolicy,
    string? VolumeBindingMode,
    bool? AllowVolumeExpansion,
    IReadOnlyDictionary<string, string> Parameters,
    IReadOnlyDictionary<string, string> Metadata
);

public sealed record KubernetesPersistentVolumeDto(
    string Name,
    string? Capacity,
    string? Status,
    string? StorageClassName,
    string? BoundClaimNamespace,
    string? BoundClaimName,
    string? ReclaimPolicy,
    KubernetesStorageOwnerDto Owner
);

public sealed record KubernetesPersistentVolumeClaimDto(
    string Name,
    string Namespace,
    string? Capacity,
    string? RequestedCapacity,
    string? Status,
    string? StorageClassName,
    string? VolumeName,
    KubernetesStorageOwnerDto Owner
);

public sealed record KubernetesStorageOwnerDto(
    string? TenantId,
    string? ResourceGroupId,
    string? ResourceType,
    string? ResourceId,
    string? ResourceName
);
