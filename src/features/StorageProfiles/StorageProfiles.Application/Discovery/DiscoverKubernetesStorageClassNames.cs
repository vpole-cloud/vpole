using k8s;
using Pole.Features.DeploymentTargets.Contracts;

namespace Pole.Features.StorageProfiles.Application.Discovery;

public union DiscoverKubernetesStorageClassNamesOutcome(
    IReadOnlySet<string>,
    KubernetesStorageDiscoveryUnavailable);

public delegate Task<DiscoverKubernetesStorageClassNamesOutcome> DiscoverKubernetesStorageClassNames(
    string deploymentTargetId,
    CancellationToken cancellationToken = default);

internal sealed class KubernetesDiscoverStorageClassNames(
    IKubernetesClientProvider kubernetesClientProvider)
{
    public async Task<DiscoverKubernetesStorageClassNamesOutcome> InvokeAsync(
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
                .Select(storageClass => storageClass.Metadata.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.Ordinal);
    }
}
