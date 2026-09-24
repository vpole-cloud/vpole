using k8s;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.StorageProfiles.Application.Common;

namespace Pole.Features.StorageProfiles.Application.Discovery;

public union DiscoverKubernetesPersistentVolumesOutcome(
    IReadOnlyCollection<KubernetesPersistentVolumeDto>,
    KubernetesStorageDiscoveryUnavailable);

public delegate Task<DiscoverKubernetesPersistentVolumesOutcome> DiscoverKubernetesPersistentVolumes(
    string deploymentTargetId,
    CancellationToken cancellationToken = default);

internal sealed class KubernetesDiscoverPersistentVolumes(
    IKubernetesClientProvider kubernetesClientProvider)
{
    public async Task<DiscoverKubernetesPersistentVolumesOutcome> InvokeAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        var resolved = await KubernetesStorageDiscovery.ResolveAsync(
            kubernetesClientProvider,
            deploymentTargetId,
            cancellationToken);
        if (resolved is KubernetesStorageDiscoveryUnavailable error)
            return error;

        var kubernetes = (IKubernetes)resolved.Value;

        var persistentVolumes = await kubernetes.CoreV1.ListPersistentVolumeAsync(
            cancellationToken: cancellationToken);
        return persistentVolumes.Items
                .OrderBy(volume => volume.Metadata.Name, StringComparer.Ordinal)
                .Select(KubernetesStorageDiscovery.MapPersistentVolume)
                .ToArray();
    }
}
