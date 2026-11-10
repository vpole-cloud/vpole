using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery.Observation;
using Pole.Features.StorageProfiles.Application.Observation;
using Pole.Features.StorageProfiles.Contracts;

namespace Pole.Features.StorageProfiles.Application.Discovery;

public union DiscoverKubernetesPersistentVolumesOutcome(
    IReadOnlyCollection<KubernetesPersistentVolumeDto>,
    KubernetesStorageDiscoveryUnavailable);

public delegate Task<DiscoverKubernetesPersistentVolumesOutcome> DiscoverKubernetesPersistentVolumes(
    string deploymentTargetId,
    CancellationToken cancellationToken = default);

internal sealed class KubernetesDiscoverPersistentVolumes(
    StorageObservationReader reader
)
{
    public async Task<DiscoverKubernetesPersistentVolumesOutcome> InvokeAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        return await reader.ReadLegacyAsync(deploymentTargetId, StorageInventoryKind.Volumes, cancellationToken) switch
        {
            StorageObservationSnapshot snapshot => snapshot.PersistentVolumes
                .Select(item => new KubernetesPersistentVolumeDto(
                    item.Name,
                    item.Capacity,
                    item.Status,
                    item.StorageClassName,
                    item.BoundClaimNamespace,
                    item.BoundClaimName,
                    item.ReclaimPolicy,
                    Owner(item.Owner)))
                .ToArray(),
            KubernetesStorageDiscoveryUnavailable unavailable => unavailable,
        };
    }

    private static KubernetesStorageOwnerDto Owner(StorageOwnerObservation owner) => new(
        owner.TenantId,
        owner.ResourceGroupId,
        owner.ResourceType,
        owner.ResourceId,
        owner.ResourceName);
}
