using k8s;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.StorageProfiles.Application.Observation;
using Pole.Features.StorageProfiles.Application.Discovery.Observation;

namespace Pole.Features.StorageProfiles.Application.Discovery;

public union DiscoverKubernetesStorageClassNamesOutcome(
    IReadOnlySet<string>,
    KubernetesStorageDiscoveryUnavailable);

public delegate Task<DiscoverKubernetesStorageClassNamesOutcome> DiscoverKubernetesStorageClassNames(
    string deploymentTargetId,
    CancellationToken cancellationToken = default);

internal sealed class KubernetesDiscoverStorageClassNames(
    StorageObservationNativeReader native,
    StorageObservationContextLoader contexts)
{
    public async Task<DiscoverKubernetesStorageClassNamesOutcome> InvokeAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        var context = await contexts.LoadAsync(deploymentTargetId, cancellationToken);
        if (context is null || await native.ReadNamesAsync(context, cancellationToken) is not { } names)
            return new KubernetesStorageDiscoveryUnavailable(deploymentTargetId);
        return names;
    }
}
