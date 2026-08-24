using k8s;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.ResultType;

namespace Pole.Features.StorageProfiles.Application.Discovery;

public delegate Task<Result<
    IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>,
    KubernetesStorageDiscoveryUnavailable>> DiscoverKubernetesPersistentVolumeClaims(
    string deploymentTargetId,
    CancellationToken cancellationToken = default);

internal sealed class KubernetesDiscoverPersistentVolumeClaims(
    IKubernetesClientProvider kubernetesClientProvider)
{
    public async Task<Result<
        IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>,
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

        var persistentVolumeClaims =
            await kubernetes.CoreV1.ListPersistentVolumeClaimForAllNamespacesAsync(
                cancellationToken: cancellationToken);
        return new Ok<IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>>(
            persistentVolumeClaims.Items
                .OrderBy(claim => claim.Metadata.NamespaceProperty, StringComparer.Ordinal)
                .ThenBy(claim => claim.Metadata.Name, StringComparer.Ordinal)
                .Select(KubernetesStorageDiscovery.MapPersistentVolumeClaim)
                .ToArray());
    }
}
