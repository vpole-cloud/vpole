using k8s;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Events;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Kubernetes;

public interface IWebServiceDeploymentApplier
{
    Task<Result> ApplyAsync(WebServiceDeploymentRequestedEvent deploymentEvent, CancellationToken cancellationToken = default);
}

public sealed class KubernetesWebServiceDeploymentApplier(
    IKubernetes kubernetes,
    IRuntimeRegistryAccessProvider registryAccessProvider) : IWebServiceDeploymentApplier
{
    public async Task<Result> ApplyAsync(WebServiceDeploymentRequestedEvent deploymentEvent, CancellationToken cancellationToken = default)
    {
        try
        {
            var metadata = new WebServiceDeploymentMetadata(
                deploymentEvent.ReleaseId,
                deploymentEvent.Revision,
                deploymentEvent.EventCore.OccurredOn);

            var webService = deploymentEvent.ToWebService().ToKubernetes();
            if (deploymentEvent.Spec.Image.SecretName is not null)
            {
                var access = await registryAccessProvider.ResolveAsync(
                    deploymentEvent.EventCore.TenantId,
                    cancellationToken);
                if (!access.TryGetValue(out var registryAccess, out var accessFailure))
                    return accessFailure;
                if (!string.Equals(
                        deploymentEvent.Spec.Image.SecretName,
                        registryAccess.KubernetesSecretName,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        deploymentEvent.Spec.Image.Server,
                        registryAccess.RegistryHost,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Failures.Conflict(
                        "The web service registry credential does not match its image registry.");
                }

                var secretResult = await WebServiceKubernetesResourceManager.ApplyImagePullSecretAsync(
                    kubernetes,
                    webService,
                    registryAccess.Username,
                    registryAccess.Secret,
                    cancellationToken);
                if (secretResult is Failure)
                    return secretResult;
            }

            return await WebServiceKubernetesResourceManager.ApplyAsync(
                kubernetes,
                webService,
                metadata,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Failures.ExternalService(
                "Unable to prepare web service deployment for Kubernetes.",
                "Kubernetes",
                "Kubernetes deployment preparation failed.");
        }
    }
}
