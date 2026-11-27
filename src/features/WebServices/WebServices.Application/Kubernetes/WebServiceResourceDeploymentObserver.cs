// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using k8s;
using k8s.Autorest;
using Pole.Features.Core.Kubernetes;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Queries;
using Pole.Features.WebServices.Domain;
using Pole.Workloads;

namespace Pole.Features.WebServices.Application.Kubernetes;

public sealed class WebServiceResourceDeploymentObserver(
    LoadWebServiceById loadResource,
    IKubernetesClientProvider clients
) : IResourceDeploymentObserver, IResourceDeploymentDiagnosticsProvider, IResourceDeploymentBoundObserver
{
    public async Task<ResourceDeploymentBoundObservation> ObserveBoundAsync(
        ResourceDeploymentObservationContext context,
        string bindingId,
        CancellationToken cancellationToken = default)
    {
        var deployment = context.Deployment;
        if (deployment.ResourceType != WebServiceResourceDeployment.ResourceType)
            return new ResourceDeploymentBoundObservation(
                null,
                new UnavailableWorkloadObservation("ResourceTypeMismatch"));
        var resource = await loadResource(deployment.TenantId, deployment.ResourceId, cancellationToken);
        if (resource is null)
            return new ResourceDeploymentBoundObservation(null, new UnavailableWorkloadObservation("ResourceNotFound"));
        var identity = new WorkloadIdentityLabels(
            deployment.TenantId,
            deployment.ResourceGroupId,
            deployment.IsolationScopeId,
            deployment.RuntimeCellId,
            deployment.Placement.DeploymentTargetId,
            WebServiceResourceDeployment.KubernetesResourceTypeLabel,
            deployment.ResourceId,
            deployment.Id);
        var client = await clients.GetAsync(deployment.Placement.DeploymentTargetId, cancellationToken);
        var namespaceName = deployment.Placement.KubernetesNamespace;
        if (!await MatchesAsync())
            return Changed();
        var observed = await KubernetesWorkloadObservation.DeploymentAsync(
            client,
            resource.Name,
            namespaceName,
            identity,
            deployment.DesiredGeneration,
            cancellationToken);
        var workload = await KubernetesWorkloadDiagnostics.DeploymentAsync(
            client,
            resource.Name,
            namespaceName,
            identity,
            deployment.DesiredGeneration,
            cancellationToken);
        if (workload?.Controller.Uid != bindingId)
            return Changed();
        var diagnostics = await KubernetesWorkloadDiagnostics.DiscoverAsync(workload, cancellationToken);
        var autoscaler = await ObserveAutoscalerAsync(
            client,
            new AutoscalerObservationRequest(resource, deployment, identity),
            cancellationToken);
        if (!await MatchesAsync())
            return Changed();
        return new ResourceDeploymentBoundObservation(bindingId, autoscaler ?? observed)
        {
            Diagnostics = diagnostics,
        };

        Task<bool> MatchesAsync() => KubernetesWorkloadObservation.MatchesDeploymentBindingAsync(
            client,
            resource.Name,
            namespaceName,
            identity,
            bindingId,
            cancellationToken);

        static ResourceDeploymentBoundObservation Changed() => new(
            null,
            new UnavailableWorkloadObservation("BindingChanged"))
        {
            Diagnostics = new UnavailableWorkloadDiagnostics("BindingChanged"),
        };
    }

    public async Task<WorkloadDiagnostics> DiscoverAsync(
        ResourceDeploymentReconciliationSnapshot deployment,
        CancellationToken cancellationToken = default) => await KubernetesWorkloadDiagnostics.DiscoverAsync(
        await ResolveDiagnosticsAsync(deployment, cancellationToken),
        cancellationToken);

    public Task<RuntimeLogOpenResult> OpenLogsAsync(
        ResourceDeploymentReconciliationSnapshot deployment,
        RuntimeLogSelection selection,
        CancellationToken cancellationToken = default) => KubernetesWorkloadDiagnostics.OpenAsync(
        ct => ResolveDiagnosticsAsync(deployment, ct),
        selection,
        cancellationToken);

    public async Task<WorkloadObservation> ObserveAsync(
        ResourceDeploymentReconciliationSnapshot deployment,
        CancellationToken cancellationToken = default)
    {
        if (deployment.ResourceType != WebServiceResourceDeployment.ResourceType)
            return new UnavailableWorkloadObservation("ResourceTypeMismatch");
        var resource = await loadResource(deployment.TenantId, deployment.ResourceId, cancellationToken);
        if (resource is null)
            return new UnavailableWorkloadObservation("ResourceNotFound");
        var identity = new WorkloadIdentityLabels(
            deployment.TenantId,
            deployment.ResourceGroupId,
            deployment.IsolationScopeId,
            deployment.RuntimeCellId,
            deployment.Placement.DeploymentTargetId,
            WebServiceResourceDeployment.KubernetesResourceTypeLabel,
            deployment.ResourceId,
            deployment.Id);
        var client = await clients.GetAsync(deployment.Placement.DeploymentTargetId, cancellationToken);
        var autoscaler = await ObserveAutoscalerAsync(
            client,
            new AutoscalerObservationRequest(resource, deployment, identity),
            cancellationToken);
        if (autoscaler is not null)
            return autoscaler;

        return await KubernetesWorkloadObservation.DeploymentAsync(
            client,
            resource.Name,
            deployment.Placement.KubernetesNamespace,
            identity,
            deployment.DesiredGeneration,
            cancellationToken);
    }

    private async Task<OwnedKubernetesWorkload?> ResolveDiagnosticsAsync(
        ResourceDeploymentReconciliationSnapshot deployment,
        CancellationToken ct)
    {
        if (deployment.ResourceType != WebServiceResourceDeployment.ResourceType)
            return null;
        var resource = await loadResource(deployment.TenantId, deployment.ResourceId, ct);
        if (resource is null)
            return null;
        var identity = new WorkloadIdentityLabels(
            deployment.TenantId,
            deployment.ResourceGroupId,
            deployment.IsolationScopeId,
            deployment.RuntimeCellId,
            deployment.Placement.DeploymentTargetId,
            WebServiceResourceDeployment.KubernetesResourceTypeLabel,
            deployment.ResourceId,
            deployment.Id);
        var client = await clients.GetAsync(deployment.Placement.DeploymentTargetId, ct);
        return await KubernetesWorkloadDiagnostics.DeploymentAsync(
            client,
            resource.Name,
            deployment.Placement.KubernetesNamespace,
            identity,
            deployment.DesiredGeneration,
            ct);
    }

    private static async Task<WorkloadObservation?> ObserveAutoscalerAsync(
        IKubernetes client,
        AutoscalerObservationRequest request,
        CancellationToken ct)
    {
        if (request.Resource.Spec.Autoscaling is not { } desired)
            return null;

        try
        {
            var native = await client.AutoscalingV2.ReadNamespacedHorizontalPodAutoscalerAsync(
                WebServiceInKubernetes.BuildAutoscalerName(request.Resource.Name),
                request.Deployment.Placement.KubernetesNamespace,
                cancellationToken: ct);
            if (native.Metadata.NamespaceProperty != request.Deployment.Placement.KubernetesNamespace ||
                !KubernetesWorkloadObservation.Owns(native.Metadata, request.Identity) ||
                native.Spec.ScaleTargetRef is not { ApiVersion: "apps/v1", Kind: "Deployment" } target ||
                target.Name != request.Resource.Name)
                return new UnavailableWorkloadObservation("AutoscalerOwnershipConflict");

            var metrics = native.Spec.Metrics;
            if (native.Metadata.DeletionTimestamp is not null || native.Spec.MinReplicas != desired.MinReplicas ||
                native.Spec.MaxReplicas != desired.MaxReplicas || metrics is null || metrics.Count != 1 ||
                metrics[0].Type != "Resource" || metrics[0].Resource?.Name != desired.Metric.Type ||
                metrics[0].Resource?.Target?.Type != "Utilization" || metrics[0].Resource?.Target?.AverageUtilization !=
                desired.Metric.TargetAverageUtilization)
                return new UnavailableWorkloadObservation("AutoscalerConfigurationDrift");

            return null;
        }
        catch (HttpOperationException error) when (error.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return new UnavailableWorkloadObservation("AutoscalerMissing");
        }
        catch (HttpOperationException)
        {
            return new UnavailableWorkloadObservation("AutoscalerUnavailable");
        }
        catch (HttpRequestException)
        {
            return new UnavailableWorkloadObservation("AutoscalerUnavailable");
        }
    }

    private sealed record AutoscalerObservationRequest(
        WebService Resource,
        ResourceDeploymentReconciliationSnapshot Deployment,
        WorkloadIdentityLabels Identity
    );
}
