using k8s;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.StorageProfiles.Application.Common;

namespace Pole.Features.StorageProfiles.Application.Discovery;

public union DiscoverKubernetesPersistentVolumeClaimsOutcome(
    IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>,
    KubernetesStorageDiscoveryUnavailable);

public delegate Task<DiscoverKubernetesPersistentVolumeClaimsOutcome> DiscoverKubernetesPersistentVolumeClaims(
    string deploymentTargetId,
    CancellationToken cancellationToken = default);

internal sealed class KubernetesDiscoverPersistentVolumeClaims(
    IKubernetesClientProvider kubernetesClientProvider)
{
    public async Task<DiscoverKubernetesPersistentVolumeClaimsOutcome> InvokeAsync(
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

        var persistentVolumeClaims =
            await kubernetes.CoreV1.ListPersistentVolumeClaimForAllNamespacesAsync(
                cancellationToken: cancellationToken);
        return persistentVolumeClaims.Items
                .OrderBy(claim => claim.Metadata.NamespaceProperty, StringComparer.Ordinal)
                .ThenBy(claim => claim.Metadata.Name, StringComparer.Ordinal)
                .Select(KubernetesStorageDiscovery.MapPersistentVolumeClaim)
                .ToArray();
    }
}
