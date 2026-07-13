using Pole.Features.StorageProfiles.Application.Common;
using Pole.ResultType;

namespace Pole.Features.StorageProfiles.Application.Discovery;

public interface IStorageDiscoveryService
{
    Task<Result<IReadOnlyCollection<KubernetesStorageClassDto>>> GetStorageClassesAsync(
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyCollection<KubernetesPersistentVolumeDto>>> GetPersistentVolumesAsync(
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>>> GetPersistentVolumeClaimsAsync(
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlySet<string>>> GetStorageClassNamesAsync(CancellationToken cancellationToken = default);
}
