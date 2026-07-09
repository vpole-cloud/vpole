using Pole.Features.StorageProfiles.Application.Common;

namespace Pole.Features.StorageProfiles.Application.Discovery;

public interface IStorageDiscoveryService
{
    Task<IReadOnlyCollection<KubernetesStorageClassDto>> GetStorageClassesAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<KubernetesPersistentVolumeDto>> GetPersistentVolumesAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>> GetPersistentVolumeClaimsAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlySet<string>> GetStorageClassNamesAsync(CancellationToken cancellationToken = default);
}
