using k8s;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Events;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Kubernetes;

public interface IWebServiceDeploymentApplier
{
    Task<Result> ApplyAsync(WebServiceDeploymentRequestedEvent deploymentEvent, CancellationToken cancellationToken = default);
}

public sealed class KubernetesWebServiceDeploymentApplier(IKubernetes kubernetes) : IWebServiceDeploymentApplier
{
    public async Task<Result> ApplyAsync(WebServiceDeploymentRequestedEvent deploymentEvent, CancellationToken cancellationToken = default)
    {
        try
        {
            var metadata = new WebServiceDeploymentMetadata(
                deploymentEvent.ReleaseId,
                deploymentEvent.Revision,
                deploymentEvent.EventCore.OccurredOn);

            return await WebServiceKubernetesResourceManager.ApplyAsync(
                kubernetes,
                deploymentEvent.ToWebService().ToKubernetes(),
                metadata,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result.ExternalService(
                "Unable to prepare web service deployment for Kubernetes.",
                "Kubernetes",
                ex.Message);
        }
    }
}
