using k8s;
using Pole.Features.DeploymentTargets.Contracts;

namespace Pole.Tests.Integration.Infrastructure;

internal sealed class FixedKubernetesClientProvider(IKubernetes kubernetes) : IKubernetesClientProvider
{
    public Task<IKubernetes> GetAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(kubernetes);

    public Task<IKubernetes> GetDefaultAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(kubernetes);
}
