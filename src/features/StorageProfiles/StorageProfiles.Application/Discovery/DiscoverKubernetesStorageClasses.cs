using k8s;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.ResultType;

namespace Pole.Features.StorageProfiles.Application.Discovery;

public delegate Task<Result<
    IReadOnlyCollection<KubernetesStorageClassDto>,
    KubernetesStorageDiscoveryUnavailable>> DiscoverKubernetesStorageClasses(
    string deploymentTargetId,
    CancellationToken cancellationToken = default);

internal sealed class KubernetesDiscoverStorageClasses(
    IKubernetesClientProvider kubernetesClientProvider)
{
    public async Task<Result<
        IReadOnlyCollection<KubernetesStorageClassDto>,
        KubernetesStorageDiscoveryUnavailable>> InvokeAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        var resolved = await KubernetesStorageDiscovery.ResolveAsync(
            kubernetesClientProvider,
            deploymentTargetId,
            cancellationToken);
        if (!resolved.TryGetValue(out var kubernetes, out var error))
            return new Err<KubernetesStorageDiscoveryUnavailable>(error);

        var storageClasses = await kubernetes.StorageV1.ListStorageClassAsync(
            cancellationToken: cancellationToken);
        return new Ok<IReadOnlyCollection<KubernetesStorageClassDto>>(
            storageClasses.Items
                .OrderBy(storageClass => storageClass.Metadata.Name, StringComparer.Ordinal)
                .Select(KubernetesStorageDiscovery.MapStorageClass)
                .ToArray());
    }
}
