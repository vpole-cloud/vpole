using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.StorageProfiles.Application.Endpoints;

public sealed record GetKubernetesPersistentVolumeClaimsRequest
    : IMediatorRequest<IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>>;

public sealed class GetKubernetesPersistentVolumeClaimsHandler(IStorageDiscoveryService storageDiscovery)
    : IMediatorHandler<GetKubernetesPersistentVolumeClaimsRequest, IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>>
{
    public async Task<Result<IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>>> HandleAsync(
        GetKubernetesPersistentVolumeClaimsRequest request,
        CancellationToken cancellationToken = default) =>
        await storageDiscovery.GetPersistentVolumeClaimsAsync(cancellationToken);
}
