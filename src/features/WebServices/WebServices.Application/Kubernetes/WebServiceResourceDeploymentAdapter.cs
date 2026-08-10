using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Domain;
using Pole.ResultType;
using Pole.Workloads;

namespace Pole.Features.WebServices.Application.Kubernetes;

public sealed class WebServiceResourceDeploymentAdapter(
    LoadWebServiceDeploymentSnapshot loadDeploymentSnapshot,
    LoadWebServiceById loadWebServiceById,
    IRuntimeRegistryAccessProvider registryAccessProvider,
    IKubernetesClientProvider kubernetesClientProvider)
    : IResourceDeploymentAdapter
{
    public async Task<ResourceDeploymentAdapterResult> ApplyAsync(
        ResourceDeploymentReconciliationContext context,
        CancellationToken cancellationToken = default)
    {
        var placementFailure = ValidateContext(context);
        if (placementFailure is not null)
            return placementFailure;

        var kubernetes = await kubernetesClientProvider.GetAsync(
            context.Deployment.Placement.DeploymentTargetId,
            cancellationToken);

        var desired = await loadDeploymentSnapshot(
            context.Deployment.TenantId,
            context.Deployment.ResourceId,
            context.Deployment.DesiredGeneration,
            cancellationToken);
        if (desired is null)
        {
            return ResourceDeploymentAdapterResult.Failed.Create(
                "WebServiceReleaseNotFound",
                "The web service release for this deployment generation no longer exists.",
                retryable: false);
        }

        var webService = desired.WebService;
        var inKubernetes = webService.ToKubernetes(
            context.Deployment.Placement.KubernetesNamespace,
            context.Deployment.Placement.Ingress!,
            context.Deployment.Placement.Compute);

        if (webService.Spec.Image.SecretName is not null)
        {
            var credentialResult = await ApplyRegistryCredentialAsync(
                context,
                kubernetes,
                webService,
                inKubernetes,
                cancellationToken);
            if (credentialResult is not null)
                return credentialResult;
        }

        var metadata = WebServiceResourceDeploymentMetadata.Create(
            desired.ReleaseId,
            context.Deployment.Id,
            context.Deployment.RuntimeCellId,
            context.Deployment.Placement.DeploymentTargetId,
            context.Deployment.DesiredGeneration,
            desired.Revision,
            desired.RequestedAtUtc);
        var applyResult = await WebServiceKubernetesResourceManager.ApplyAsync(
            kubernetes,
            inKubernetes,
            metadata,
            cancellationToken);
        if (applyResult is Failure)
        {
            return ResourceDeploymentAdapterResult.Failed.Create(
                "KubernetesApplyFailed",
                "Kubernetes could not apply the web service deployment.",
                retryable: true);
        }

        return ResourceDeploymentAdapterResult.Succeeded.Create(
        [
            ResourceDeploymentEndpointDto.Create(
                "Ingress",
                webService.DnsRecord.Domain,
                port: 443,
                scheme: "https")
        ]);
    }

    public async Task<ResourceDeploymentAdapterResult> DeleteAsync(
        ResourceDeploymentReconciliationContext context,
        CancellationToken cancellationToken = default)
    {
        var placementFailure = ValidateContext(context);
        if (placementFailure is not null)
            return placementFailure;

        var kubernetes = await kubernetesClientProvider.GetAsync(
            context.Deployment.Placement.DeploymentTargetId,
            cancellationToken);

        var webService = await loadWebServiceById(
            context.Deployment.TenantId,
            context.Deployment.ResourceId,
            cancellationToken);
        if (webService is null)
        {
            return ResourceDeploymentAdapterResult.Failed.Create(
                "WebServiceNotFound",
                "The logical web service no longer exists.",
                retryable: false);
        }

        await WebServiceKubernetesResourceManager.DeleteAsync(
            kubernetes,
            webService.Name,
            context.Deployment.Placement.KubernetesNamespace,
            cancellationToken);

        return ResourceDeploymentAdapterResult.Succeeded.Create();
    }

    private static ResourceDeploymentAdapterResult.Failed? ValidateContext(
        ResourceDeploymentReconciliationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(
                context.Deployment.ResourceType.Value,
                WebServiceResourceDeployment.ResourceType.Value,
                StringComparison.Ordinal))
        {
            return ResourceDeploymentAdapterResult.Failed.Create(
                "ResourceTypeMismatch",
                "The resource deployment is not a web service.",
                retryable: false);
        }

        var ingress = context.Deployment.Placement.Ingress;
        if (ingress is null)
        {
            return ResourceDeploymentAdapterResult.Failed.Create(
                "IngressConfigurationMissing",
                "The web service placement does not contain resolved ingress configuration.",
                retryable: false);
        }

        return null;
    }

    private async Task<ResourceDeploymentAdapterResult.Failed?> ApplyRegistryCredentialAsync(
        ResourceDeploymentReconciliationContext context,
        k8s.IKubernetes kubernetes,
        WebService webService,
        WebServiceInKubernetes inKubernetes,
        CancellationToken cancellationToken)
    {
        var access = await registryAccessProvider.ResolveAsync(
            context.Deployment.TenantId,
            cancellationToken);
        if (!access.TryGetValue(out var registryAccess, out _))
        {
            return ResourceDeploymentAdapterResult.Failed.Create(
                "RegistryCredentialUnavailable",
                "The web service registry credential is unavailable.",
                retryable: true);
        }

        if (!string.Equals(
                webService.Spec.Image.SecretName,
                registryAccess.KubernetesSecretName,
                StringComparison.Ordinal) ||
            !string.Equals(
                webService.Spec.Image.Server,
                registryAccess.RegistryHost,
                StringComparison.OrdinalIgnoreCase))
        {
            return ResourceDeploymentAdapterResult.Failed.Create(
                "RegistryCredentialMismatch",
                "The web service registry credential does not match its image registry.",
                retryable: false);
        }

        var secretResult = await WebServiceKubernetesResourceManager.ApplyImagePullSecretAsync(
            kubernetes,
            inKubernetes,
            registryAccess.Username,
            registryAccess.Secret,
            cancellationToken);
        return secretResult is Failure
            ? ResourceDeploymentAdapterResult.Failed.Create(
                "RegistrySecretApplyFailed",
                "Kubernetes could not apply the web service registry credential.",
                retryable: true)
            : null;
    }

}

public static class WebServiceResourceDeploymentAdapterRegistration
{
    public static ResourceDeploymentAdapterDescriptor Descriptor { get; } =
        ResourceDeploymentAdapterDescriptor.Create<WebServiceResourceDeploymentAdapter>(
            WebServiceResourceDeployment.ResourceType,
            ResourcePlacementCapability.SingleTarget);
}
