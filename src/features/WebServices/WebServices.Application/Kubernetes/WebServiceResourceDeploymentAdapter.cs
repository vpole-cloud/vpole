using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.ResourceDeployments.Application.Reconciliation;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Domain;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Kubernetes;

public interface IWebServiceResourceDeploymentReader
{
    Task<WebServiceResourceDeploymentSnapshot?> FindForApplyAsync(
        string tenantId,
        string webServiceId,
        long deploymentGeneration,
        CancellationToken cancellationToken = default);

    Task<WebService?> FindForDeleteAsync(
        string tenantId,
        string webServiceId,
        CancellationToken cancellationToken = default);
}

public sealed record WebServiceResourceDeploymentSnapshot(
    WebService WebService,
    string ReleaseId,
    int Revision,
    DateTime RequestedAtUtc);

public sealed class MongoWebServiceResourceDeploymentReader(
    IWebServiceDbContext dbContext) : IWebServiceResourceDeploymentReader
{
    public async Task<WebServiceResourceDeploymentSnapshot?> FindForApplyAsync(
        string tenantId,
        string webServiceId,
        long deploymentGeneration,
        CancellationToken cancellationToken = default)
    {
        var webService = await FindForDeleteAsync(
            tenantId,
            webServiceId,
            cancellationToken);
        if (webService is null)
            return null;

        var release = await dbContext.WebServiceReleases
            .Find(Builders<WebServiceRelease>.Filter.And(
                Builders<WebServiceRelease>.Filter.ForTenant(tenantId),
                Builders<WebServiceRelease>.Filter.Eq(
                    item => item.WebServiceId,
                    webServiceId),
                Builders<WebServiceRelease>.Filter.Eq(
                    item => item.DeploymentGeneration,
                    deploymentGeneration)))
            .FirstOrDefaultAsync(cancellationToken);
        if (release is null)
            return null;

        return new WebServiceResourceDeploymentSnapshot(
            webService with
            {
                Spec = release.Spec.ToDomain(),
                ActiveRevision = release.Revision
            },
            release.Id,
            release.Revision,
            release.RequestedAtUtc);
    }

    public async Task<WebService?> FindForDeleteAsync(
        string tenantId,
        string webServiceId,
        CancellationToken cancellationToken = default) =>
        await dbContext.WebServices
            .Find(Builders<WebService>.Filter.And(
                Builders<WebService>.Filter.ForTenant(tenantId),
                Builders<WebService>.Filter.Eq(
                    webService => webService.Id,
                    webServiceId)))
            .FirstOrDefaultAsync(cancellationToken);
}

public sealed class WebServiceResourceDeploymentAdapter(
    IWebServiceResourceDeploymentReader reader,
    IRuntimeRegistryAccessProvider registryAccessProvider)
    : IResourceDeploymentAdapter
{
    public async Task<ResourceDeploymentAdapterResult> ApplyAsync(
        ResourceDeploymentReconciliationContext context,
        CancellationToken cancellationToken = default)
    {
        var placementFailure = ValidateContext(context);
        if (placementFailure is not null)
            return placementFailure;

        var desired = await reader.FindForApplyAsync(
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
            context.Deployment.ResolvedPlacement.KubernetesNamespace,
            context.Deployment.ResolvedPlacement.Ingress!);

        if (webService.Spec.Image.SecretName is not null)
        {
            var credentialResult = await ApplyRegistryCredentialAsync(
                context,
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
            context.Deployment.ResolvedPlacement.DeploymentTargetId,
            context.Deployment.DesiredGeneration,
            desired.Revision,
            desired.RequestedAtUtc);
        var applyResult = await WebServiceKubernetesResourceManager.ApplyAsync(
            context.Kubernetes,
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
            IngressResourceDeploymentEndpoint.Create(
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

        var webService = await reader.FindForDeleteAsync(
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
            context.Kubernetes,
            webService.Name,
            context.Deployment.ResolvedPlacement.KubernetesNamespace,
            cancellationToken);

        return ResourceDeploymentAdapterResult.Succeeded.Create();
    }

    private static ResourceDeploymentAdapterResult.Failed? ValidateContext(
        ResourceDeploymentReconciliationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(
                context.Deployment.ResourceType,
                ResourceDeploymentResourceTypes.WebService.Value,
                StringComparison.Ordinal))
        {
            return ResourceDeploymentAdapterResult.Failed.Create(
                "ResourceTypeMismatch",
                "The resource deployment is not a web service.",
                retryable: false);
        }

        var ingress = context.Deployment.ResolvedPlacement.Ingress;
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
            context.Kubernetes,
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
