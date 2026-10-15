using Pole.Features.Core.Kubernetes;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Persistence.Reads;
using Pole.Workloads;

namespace Pole.Features.RedisServices.Application.Kubernetes;

public sealed class RedisResourceDeploymentObserver(
    LoadRedisById loadResource,
    IKubernetesClientProvider clients) : IResourceDeploymentObserver, IResourceDeploymentDiagnosticsProvider,
    IResourceDeploymentBoundObserver
{
    private async Task<OwnedKubernetesWorkload?> ResolveDiagnosticsAsync(
        ResourceDeploymentReconciliationSnapshot deployment,
        CancellationToken ct)
    {
        if (deployment.ResourceType != RedisResourceDeployment.ResourceType)
            return null;
        var resource = await loadResource(deployment.TenantId, deployment.ResourceId, ct);
        if (resource is null)
            return null;
        var identity = new WorkloadIdentityLabels(deployment.TenantId,
            deployment.ResourceGroupId,
            deployment.IsolationScopeId,
            deployment.RuntimeCellId,
            deployment.Placement.DeploymentTargetId,
            RedisResourceDeployment.KubernetesResourceTypeLabel,
            deployment.ResourceId,
            deployment.Id);
        var client = await clients.GetAsync(deployment.Placement.DeploymentTargetId, ct);
        return await KubernetesWorkloadDiagnostics.StatefulSetAsync(client,
            resource.Name,
            deployment.Placement.KubernetesNamespace,
            identity,
            deployment.DesiredGeneration,
            KubernetesWorkloadObservation.TemplateGenerationAnnotation,
            false,
            ct);
    }

    public async Task<WorkloadDiagnostics> DiscoverAsync(ResourceDeploymentReconciliationSnapshot deployment,
        CancellationToken cancellationToken = default) =>
        await KubernetesWorkloadDiagnostics.DiscoverAsync(await ResolveDiagnosticsAsync(deployment, cancellationToken),
            cancellationToken);

    public Task<RuntimeLogOpenResult> OpenLogsAsync(ResourceDeploymentReconciliationSnapshot deployment,
        RuntimeLogSelection selection,
        CancellationToken cancellationToken = default) =>
        KubernetesWorkloadDiagnostics.OpenAsync(ct => ResolveDiagnosticsAsync(deployment, ct),
            selection,
            cancellationToken);

    public async Task<ResourceDeploymentBoundObservation> ObserveBoundAsync(
        ResourceDeploymentObservationContext context,
        string bindingId,
        CancellationToken cancellationToken = default)
    {
        var deployment = context.Deployment;
        if (deployment.ResourceType != RedisResourceDeployment.ResourceType)
            return new(null, new UnavailableWorkloadObservation("ResourceTypeMismatch"));
        var resource = await loadResource(deployment.TenantId, deployment.ResourceId, cancellationToken);
        if (resource is null)
            return new(null, new UnavailableWorkloadObservation("ResourceNotFound"));
        var identity = new WorkloadIdentityLabels(deployment.TenantId,
            deployment.ResourceGroupId,
            deployment.IsolationScopeId,
            deployment.RuntimeCellId,
            deployment.Placement.DeploymentTargetId,
            RedisResourceDeployment.KubernetesResourceTypeLabel,
            deployment.ResourceId,
            deployment.Id);
        var client = await clients.GetAsync(deployment.Placement.DeploymentTargetId, cancellationToken);
        var namespaceName = deployment.Placement.KubernetesNamespace;
        if (!await MatchesAsync())
            return Changed();
        var observed = await KubernetesWorkloadObservation.StatefulSetAsync(client,
            resource.Name,
            namespaceName,
            identity,
            deployment.DesiredGeneration,
            KubernetesWorkloadObservation.TemplateGenerationAnnotation,
            false,
            cancellationToken);
        var workload = await KubernetesWorkloadDiagnostics.StatefulSetAsync(client,
            resource.Name,
            namespaceName,
            identity,
            deployment.DesiredGeneration,
            KubernetesWorkloadObservation.TemplateGenerationAnnotation,
            false,
            cancellationToken);
        if (workload?.Controller.Uid != bindingId)
            return Changed();
        var diagnostics = await KubernetesWorkloadDiagnostics.DiscoverAsync(workload, cancellationToken);
        if (!await MatchesAsync())
            return Changed();
        return new(bindingId, observed)
        {
            Diagnostics = diagnostics
        };

        Task<bool> MatchesAsync() => KubernetesWorkloadObservation.MatchesStatefulSetBindingAsync(client,
            resource.Name,
            namespaceName,
            identity,
            bindingId,
            cancellationToken);

        static ResourceDeploymentBoundObservation Changed() =>
            new(null, new UnavailableWorkloadObservation("BindingChanged"))
            {
                Diagnostics = new UnavailableWorkloadDiagnostics("BindingChanged")
            };
    }

    public async Task<WorkloadObservation> ObserveAsync(ResourceDeploymentReconciliationSnapshot deployment,
        CancellationToken cancellationToken = default)
    {
        if (deployment.ResourceType != RedisResourceDeployment.ResourceType)
            return new UnavailableWorkloadObservation("ResourceTypeMismatch");
        var resource = await loadResource(deployment.TenantId, deployment.ResourceId, cancellationToken);
        if (resource is null)
            return new UnavailableWorkloadObservation("ResourceNotFound");
        var identity = new WorkloadIdentityLabels(deployment.TenantId,
            deployment.ResourceGroupId,
            deployment.IsolationScopeId,
            deployment.RuntimeCellId,
            deployment.Placement.DeploymentTargetId,
            RedisResourceDeployment.KubernetesResourceTypeLabel,
            deployment.ResourceId,
            deployment.Id);
        var client = await clients.GetAsync(deployment.Placement.DeploymentTargetId, cancellationToken);
        return await KubernetesWorkloadObservation.StatefulSetAsync(client,
            resource.Name,
            deployment.Placement.KubernetesNamespace,
            identity,
            deployment.DesiredGeneration,
            KubernetesWorkloadObservation.TemplateGenerationAnnotation,
            false,
            cancellationToken);
    }
}
