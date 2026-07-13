using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.StorageProfiles.Application.Endpoints;

public sealed record GetKubernetesPersistentVolumesRequest
    : IMediatorRequest<IReadOnlyCollection<KubernetesPersistentVolumeDto>>;

public sealed class GetKubernetesPersistentVolumesHandler(IStorageDiscoveryService storageDiscovery)
    : IMediatorHandler<GetKubernetesPersistentVolumesRequest, IReadOnlyCollection<KubernetesPersistentVolumeDto>>
{
    public async Task<Result<IReadOnlyCollection<KubernetesPersistentVolumeDto>>> HandleAsync(
        GetKubernetesPersistentVolumesRequest request,
        CancellationToken cancellationToken = default) =>
        await storageDiscovery.GetPersistentVolumesAsync(cancellationToken);
}
