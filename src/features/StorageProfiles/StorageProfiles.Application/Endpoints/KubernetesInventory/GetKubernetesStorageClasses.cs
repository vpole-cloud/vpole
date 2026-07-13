using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.StorageProfiles.Application.Endpoints;

public sealed record GetKubernetesStorageClassesRequest
    : IMediatorRequest<IReadOnlyCollection<KubernetesStorageClassDto>>;

public sealed class GetKubernetesStorageClassesHandler(IStorageDiscoveryService storageDiscovery)
    : IMediatorHandler<GetKubernetesStorageClassesRequest, IReadOnlyCollection<KubernetesStorageClassDto>>
{
    public async Task<Result<IReadOnlyCollection<KubernetesStorageClassDto>>> HandleAsync(
        GetKubernetesStorageClassesRequest request,
        CancellationToken cancellationToken = default) =>
        (await storageDiscovery.GetStorageClassesAsync(cancellationToken)).ToArray();
}
