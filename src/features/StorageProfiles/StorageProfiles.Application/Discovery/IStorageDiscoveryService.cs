using Pole.Features.StorageProfiles.Application.Common;
using Pole.ResultType;

namespace Pole.Features.StorageProfiles.Application.Discovery;

public interface IStorageDiscoveryService
{
    Task<Result<IReadOnlyCollection<KubernetesStorageClassDto>>> GetStorageClassesAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyCollection<KubernetesPersistentVolumeDto>>> GetPersistentVolumesAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>>> GetPersistentVolumeClaimsAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlySet<string>>> GetStorageClassNamesAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default);
}
