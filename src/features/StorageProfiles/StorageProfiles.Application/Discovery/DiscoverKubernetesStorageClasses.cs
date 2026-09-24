using k8s;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.StorageProfiles.Application.Common;

namespace Pole.Features.StorageProfiles.Application.Discovery;

public union DiscoverKubernetesStorageClassesOutcome(
    IReadOnlyCollection<KubernetesStorageClassDto>,
    KubernetesStorageDiscoveryUnavailable);

public delegate Task<DiscoverKubernetesStorageClassesOutcome> DiscoverKubernetesStorageClasses(
    string deploymentTargetId,
    CancellationToken cancellationToken = default);

internal sealed class KubernetesDiscoverStorageClasses(
    IKubernetesClientProvider kubernetesClientProvider)
{
    public async Task<DiscoverKubernetesStorageClassesOutcome> InvokeAsync(
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

        var storageClasses = await kubernetes.StorageV1.ListStorageClassAsync(
            cancellationToken: cancellationToken);
        return storageClasses.Items
                .OrderBy(storageClass => storageClass.Metadata.Name, StringComparer.Ordinal)
                .Select(KubernetesStorageDiscovery.MapStorageClass)
                .ToArray();
    }
}
