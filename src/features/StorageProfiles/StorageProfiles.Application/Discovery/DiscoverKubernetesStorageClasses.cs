using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery.Observation;
using Pole.Features.StorageProfiles.Application.Observation;
using Pole.Features.StorageProfiles.Contracts;

namespace Pole.Features.StorageProfiles.Application.Discovery;

public union DiscoverKubernetesStorageClassesOutcome(
    IReadOnlyCollection<KubernetesStorageClassDto>,
    KubernetesStorageDiscoveryUnavailable);

public delegate Task<DiscoverKubernetesStorageClassesOutcome> DiscoverKubernetesStorageClasses(
    string deploymentTargetId,
    CancellationToken cancellationToken = default);

internal sealed class KubernetesDiscoverStorageClasses(
    StorageObservationReader reader
)
{
    public async Task<DiscoverKubernetesStorageClassesOutcome> InvokeAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        return await reader.ReadLegacyAsync(deploymentTargetId, StorageInventoryKind.Classes, cancellationToken) switch
        {
            StorageObservationSnapshot snapshot => snapshot.StorageClasses
                .Select(item => new KubernetesStorageClassDto(
                    item.Name,
                    item.Provisioner,
                    item.IsDefault,
                    item.ReclaimPolicy,
                    item.VolumeBindingMode,
                    item.AllowVolumeExpansion,
                    new Dictionary<string, string>(),
                    new Dictionary<string, string>()))
                .ToArray(),
            KubernetesStorageDiscoveryUnavailable unavailable => unavailable,
        };
    }
}
