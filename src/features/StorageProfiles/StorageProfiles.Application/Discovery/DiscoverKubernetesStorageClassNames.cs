using k8s;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.ResultType;

namespace Pole.Features.StorageProfiles.Application.Discovery;

public delegate Task<Result<
    IReadOnlySet<string>,
    KubernetesStorageDiscoveryUnavailable>> DiscoverKubernetesStorageClassNames(
    string deploymentTargetId,
    CancellationToken cancellationToken = default);

internal sealed class KubernetesDiscoverStorageClassNames(
    IKubernetesClientProvider kubernetesClientProvider)
{
    public async Task<Result<
        IReadOnlySet<string>,
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
        return new Ok<IReadOnlySet<string>>(
            storageClasses.Items
                .Select(storageClass => storageClass.Metadata.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.Ordinal));
    }
}
