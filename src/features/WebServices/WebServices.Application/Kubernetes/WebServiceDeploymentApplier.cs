using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.WebServices.Events;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Kubernetes;

public interface IWebServiceDeploymentApplier
{
    Task<Result> ApplyAsync(WebServiceDeploymentRequestedEvent deploymentEvent, CancellationToken cancellationToken = default);
}

public sealed class KubernetesWebServiceDeploymentApplier(
    IKubernetesClientProvider kubernetesClientProvider,
    IRuntimeRegistryAccessProvider registryAccessProvider) : IWebServiceDeploymentApplier
{
    public Task<Result> ApplyAsync(
        WebServiceDeploymentRequestedEvent deploymentEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deploymentEvent);
        _ = kubernetesClientProvider;
        _ = registryAccessProvider;
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<Result>(Failures.Conflict(
            "Legacy web service deployment events no longer contain placement. " +
            "Use the ResourceDeployment reconciliation adapter."));
    }
}
