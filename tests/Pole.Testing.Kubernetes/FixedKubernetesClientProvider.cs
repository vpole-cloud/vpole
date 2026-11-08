using k8s;
using Pole.Features.DeploymentTargets.Contracts;

namespace Pole.Testing.Kubernetes;

public sealed class FixedKubernetesClientProvider(IKubernetes kubernetes) : IKubernetesClientProvider
{
    public Task<IKubernetes> GetAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(kubernetes);

    public Task<IKubernetes> GetForCleanupAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default) =>
        GetAsync(deploymentTargetId, cancellationToken);

    public Task<IKubernetes> GetDefaultAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(kubernetes);
}
