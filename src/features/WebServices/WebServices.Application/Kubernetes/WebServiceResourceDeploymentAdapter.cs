using System.Collections.Immutable;
using Pole.Features.Core.Kubernetes;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.Secrets.Contracts;
using k8s.Models;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Application.Persistence.Deployments;
using Pole.Features.WebServices.Application.Persistence.Reads;
using Pole.Features.WebServices.Domain;
using Pole.Workloads;

namespace Pole.Features.WebServices.Application.Kubernetes;

public sealed class WebServiceResourceDeploymentAdapter(
    LoadWebServiceDeploymentSnapshot loadDeploymentSnapshot,
    LoadWebServiceById loadWebServiceById,
    LoadExactResourceDeployment loadExactResourceDeployment,
    ResolveRuntimeRegistryAccess resolveRuntimeRegistryAccess,
    IKubernetesClientProvider kubernetesClientProvider,
    ReconcileResourceIngressPolicy reconcileResourceIngressPolicy,
    ResolveResourceConnectionReferences resolveConnectionReferences,
    EnsureConsumerSecretProjections ensureSecretProjections)
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
            context.Deployment.Id,
            context.Deployment.ResourceRevision,
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
        var resolvedIngress = context.Deployment.Placement.Ingress;
        if (webService.Exposure.Mode is WebServiceExposureMode.Public &&
            (resolvedIngress is null || resolvedIngress.Hostnames.Count == 0))
        {
            return ResourceDeploymentAdapterResult.Failed.Create(
                "IngressConfigurationMissing",
                "The public web service placement does not contain resolved ingress configuration.",
                retryable: false);
        }
        if (webService.Exposure.Mode is WebServiceExposureMode.Private && resolvedIngress is not null)
        {
            return ResourceDeploymentAdapterResult.Failed.Create(
                "PrivateIngressConfigurationUnexpected",
                "The private web service placement contains ingress configuration.",
                retryable: false);
        }
        var allowedTcpPorts = ResolvePublicIngressTargetPort(webService);
        if (allowedTcpPorts is null)
        {
            return ResourceDeploymentAdapterResult.Failed.Create(
                "PublicIngressTargetPortUnavailable",
                "The public web-service target port could not be resolved.",
                retryable: false);
        }
        var connectionReferences = webService.Spec.Config.ConnectionEnvironmentReferences;
        var connectionValues = await resolveConnectionReferences(
            new ResolveResourceConnectionReferencesRequest(
                TenantId: context.Deployment.TenantId,
                SourceType: WebServiceResourceDeployment.ResourceType,
                SourceId: webService.Id,
                DeploymentId: context.Deployment.Id,
                References: connectionReferences.Values.Select(reference => new ResourceConnectionValueReference(reference.ConnectionId, reference.ValueKind, reference.ConfigurationKey)).ToArray()),
            ct: cancellationToken);
        if (connectionValues is ResourceConnectionReferencesUnavailable)
            return ResourceDeploymentAdapterResult.Failed.Create("ConnectionConfigurationUnavailable", "A configured connection or field is no longer available for this deployment.", retryable: true);
        var secretReferences = webService.Spec.Config.SecretEnvironmentReferences;
        var projectedSecrets = await ensureSecretProjections(
            new EnsureConsumerSecretProjectionsRequest(
                TenantId: context.Deployment.TenantId,
                ConsumerType: WebServiceResourceDeployment.ResourceType,
                ConsumerId: webService.Id,
                DeploymentId: context.Deployment.Id,
                References: secretReferences.Values.Select(reference => new ConsumerSecretKeyReference(reference.SecretId, reference.Key)).ToArray()),
            ct: cancellationToken);
        if (projectedSecrets is ConsumerSecretReferenceUnavailable)
            return ResourceDeploymentAdapterResult.Failed.Create("SecretConfigurationUnavailable", "A configured Secret key or binding is no longer authorized for this deployment.", retryable: true);
        if (projectedSecrets is ConsumerSecretProjectionPending)
            return ResourceDeploymentAdapterResult.Failed.Create("SecretProjectionPending", "An authorized Secret projection is not ready on this target. Retry after projection completes.", retryable: true);
        var resolvedEnvironment = ImmutableList.CreateBuilder<V1EnvVar>();
        foreach (var (name, reference) in connectionReferences)
            resolvedEnvironment.Add(new V1EnvVar
            {
                Name = name,
                Value = ((ResolvedResourceConnectionValues)connectionValues.Value).Values.Single(value =>
                value.Reference == new ResourceConnectionValueReference(reference.ConnectionId, reference.ValueKind, reference.ConfigurationKey)).Value
            });
        foreach (var (name, reference) in secretReferences)
        {
            var key = ((ConsumerSecretProjectionKeys)projectedSecrets.Value).Keys.Single(key => key.SecretId == reference.SecretId && key.SourceKey == reference.Key);
            resolvedEnvironment.Add(new V1EnvVar { Name = name, ValueFrom = new V1EnvVarSource { SecretKeyRef = new V1SecretKeySelector { Key = key.ProjectedKey, Name = key.KubernetesName, Optional = false } } });
        }
        var inKubernetes = webService.ToKubernetes(
            context.Deployment.Placement.KubernetesNamespace,
            context.Deployment.Placement.Ingress,
            context.Deployment.Placement.Compute,
            context.Deployment.FixedReplicas,
            new WorkloadIdentityLabels(
                context.Deployment.TenantId,
                context.Deployment.ResourceGroupId,
                context.Deployment.IsolationScopeId,
                context.Deployment.RuntimeCellId,
                context.Deployment.Placement.DeploymentTargetId,
                WebServiceResourceDeployment.KubernetesResourceTypeLabel,
                context.Deployment.ResourceId,
                context.Deployment.Id)) with
        { ResolvedEnvironment = resolvedEnvironment.ToImmutable() };
        var metadata = WebServiceResourceDeploymentMetadata.Create(
            desired.ReleaseId,
            context.Deployment.Id,
            context.Deployment.RuntimeCellId,
            context.Deployment.Placement.DeploymentTargetId,
            context.Deployment.DesiredGeneration,
            desired.ResourceRevision,
            desired.RequestedAtUtc);
        var policyFailure = await ReconcileIngressPoliciesAsync(
            context,
            webService.Exposure.Mode is WebServiceExposureMode.Public ? allowedTcpPorts : [],
            webService.Exposure.Mode is WebServiceExposureMode.Public ? [NetworkPort.Create(8089)] : [],
            cancellationToken);
        if (webService.Exposure.Mode is WebServiceExposureMode.Private)
        {
            if (policyFailure is { Retryable: false })
                return policyFailure;
            if (!await IsCurrentGenerationAsync(context, cancellationToken))
            {
                return ResourceDeploymentAdapterResult.Failed.Create(
                    "DeploymentGenerationSuperseded",
                    "A newer deployment generation superseded this ingress revocation.",
                    retryable: true);
            }
            var ingressRevoked = await WebServiceKubernetesResourceManager.DeleteIngressForGenerationAsync(
                kubernetes,
                inKubernetes,
                metadata,
                cancellationToken,
                ct => IsCurrentGenerationAsync(context, ct));
            if (!ingressRevoked)
            {
                return ResourceDeploymentAdapterResult.Failed.Create(
                    "IngressRevocationPending",
                    "The private ingress revocation has not been confirmed for this deployment generation.",
                    retryable: true);
            }
        }
        if (policyFailure is not null)
            return policyFailure;

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

        var applyResult = await WebServiceKubernetesResourceManager.ApplyAsync(
            kubernetes,
            inKubernetes,
            metadata,
            cancellationToken,
            ingressAlreadyRemoved: webService.Exposure.Mode is WebServiceExposureMode.Private,
            isCurrentGeneration: webService.Exposure.Mode is WebServiceExposureMode.Public
                ? ct => IsCurrentGenerationAsync(context, ct)
                : null);
        if (applyResult is WebServiceKubernetesApplyFailed)
        {
            return ResourceDeploymentAdapterResult.Failed.Create(
                "KubernetesApplyFailed",
                "Kubernetes could not apply the web service deployment.",
                retryable: true);
        }
        if (applyResult is WebServiceKubernetesApplyRejected rejected)
        {
            return ResourceDeploymentAdapterResult.Failed.Create(
                rejected.FailureCode,
                rejected.Message,
                retryable: true);
        }

        return ResourceDeploymentAdapterResult.Succeeded.Create(
            webService.Exposure.Mode is WebServiceExposureMode.Private
                ? []
                : context.Deployment.Placement.Ingress!.Hostnames.Select(hostname =>
                ResourceDeploymentEndpointDto.Create(
                    "Ingress",
                    hostname,
                    port: 443,
                    scheme: "https")));
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

        var policyCleanupFailure = await ReconcileIngressPoliciesAsync(
            context,
            [],
            [],
            cancellationToken);
        if (policyCleanupFailure is not null)
            return policyCleanupFailure;

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
        if (ingress is not null && ingress.Hostnames.Count == 0)
        {
            return ResourceDeploymentAdapterResult.Failed.Create(
                "IngressHostnamesMissing",
                "The web service placement does not contain an ingress hostname.",
                retryable: false);
        }

        return null;
    }

    private static ImmutableList<NetworkPort>? ResolvePublicIngressTargetPort(WebService webService)
    {
        if (webService.Exposure.Mode is WebServiceExposureMode.Private)
            return [];
        var mapping = webService.Spec.Config.PortMappings.FirstOrDefault();
        return mapping is not null &&
               WebServicePublicIngressPort.TryResolve(mapping.Protocol, mapping.TargetPort, out var port)
            ? [port]
            : null;
    }

    private async Task<bool> IsCurrentGenerationAsync(
        ResourceDeploymentReconciliationContext context,
        CancellationToken cancellationToken)
    {
        var current = await loadExactResourceDeployment(
            new LoadExactResourceDeploymentRequest(
                TenantId: context.Deployment.TenantId,
                ResourceType: context.Deployment.ResourceType,
                ResourceId: context.Deployment.ResourceId,
                DeploymentId: context.Deployment.Id),
            cancellationToken: cancellationToken);
        return current?.DesiredGeneration == context.Deployment.DesiredGeneration;
    }

    private async Task<ResourceDeploymentAdapterResult.Failed?> ReconcileIngressPoliciesAsync(
        ResourceDeploymentReconciliationContext context,
        ImmutableList<NetworkPort> publicTcpPorts,
        ImmutableList<NetworkPort> http01TcpPorts,
        CancellationToken cancellationToken)
    {
        ResourceDeploymentAdapterResult.Failed? failure = null;
        foreach (var (purpose, ports) in new[]
                 {
                     (WebServiceIngressPolicyDefinitions.PublicPurpose, publicTcpPorts),
                     (WebServiceIngressPolicyDefinitions.Http01Purpose, http01TcpPorts)
                 })
        {
            var result = await reconcileResourceIngressPolicy(
                new ResourceIngressPolicyRequest(
                    context.Deployment.TenantId,
                    context.Deployment.ResourceType,
                    context.Deployment.ResourceId,
                    context.Deployment.Id,
                    context.Deployment.RuntimeCellId,
                    context.Deployment.Placement.DeploymentTargetId,
                    context.Deployment.Placement.KubernetesNamespace,
                    purpose,
                    context.Deployment.DesiredGeneration,
                    context.Deployment.Placement.TargetWorkloadConfigurationRevision,
                    ports),
                cancellationToken);
            if (result is not ResourceIngressPolicyApplied)
            {
                var ownershipConflict = result is ResourceIngressPolicyOwnershipConflict;
                failure ??= ResourceDeploymentAdapterResult.Failed.Create(
                    result is ResourceIngressPolicySourceUnavailable
                        ? "PublicIngressSourceUnavailable"
                        : "IngressPolicyReconciliationFailed",
                    "Kubernetes could not reconcile the web-service ingress policy.",
                    retryable: !ownershipConflict);
            }
        }
        return failure;
    }

    private async Task<ResourceDeploymentAdapterResult.Failed?> ApplyRegistryCredentialAsync(
        ResourceDeploymentReconciliationContext context,
        k8s.IKubernetes kubernetes,
        WebService webService,
        WebServiceInKubernetes inKubernetes,
        CancellationToken cancellationToken)
    {
        var access = await resolveRuntimeRegistryAccess(
            context.Deployment.TenantId,
            cancellationToken);
        if (access.Value is not RuntimeRegistryAccess registryAccess)
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

        await WebServiceKubernetesResourceManager.ApplyImagePullSecretAsync(
            kubernetes,
            inKubernetes,
            registryAccess.Username,
            registryAccess.Secret,
            cancellationToken);
        return null;
    }

}

public static class WebServiceResourceDeploymentAdapterRegistration
{
    public static ResourceDeploymentAdapterDescriptor Descriptor { get; } =
        ResourceDeploymentAdapterDescriptor.Create<WebServiceResourceDeploymentAdapter>(
            WebServiceResourceDeployment.ResourceType,
            ResourcePlacementCapability.MultiTarget);
}
