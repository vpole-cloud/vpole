using k8s;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.ResultType;

namespace Pole.Features.StorageProfiles.Application.Discovery;

public delegate Task<Result<
    IReadOnlyCollection<KubernetesPersistentVolumeDto>,
    KubernetesStorageDiscoveryUnavailable>> DiscoverKubernetesPersistentVolumes(
    string deploymentTargetId,
    CancellationToken cancellationToken = default);

internal sealed class KubernetesDiscoverPersistentVolumes(
    IKubernetesClientProvider kubernetesClientProvider)
{
    public async Task<Result<
        IReadOnlyCollection<KubernetesPersistentVolumeDto>,
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

        var persistentVolumes = await kubernetes.CoreV1.ListPersistentVolumeAsync(
            cancellationToken: cancellationToken);
        return new Ok<IReadOnlyCollection<KubernetesPersistentVolumeDto>>(
            persistentVolumes.Items
                .OrderBy(volume => volume.Metadata.Name, StringComparer.Ordinal)
                .Select(KubernetesStorageDiscovery.MapPersistentVolume)
                .ToArray());
    }
}
