using Pole.Features.Core.Kubernetes;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence.Reads;
using Pole.Workloads;

namespace Pole.Features.WebServices.Application.Kubernetes;

public sealed class WebServiceResourceDeploymentObserver(
    LoadWebServiceById loadResource,
    IKubernetesClientProvider clients) : IResourceDeploymentObserver, IResourceDeploymentDiagnosticsProvider
{
    private async Task<OwnedKubernetesWorkload?> ResolveDiagnosticsAsync(ResourceDeploymentReconciliationSnapshot deployment, CancellationToken ct)
    {
        if (deployment.ResourceType != WebServiceResourceDeployment.ResourceType) return null;
        var resource = await loadResource(deployment.TenantId, deployment.ResourceId, ct);
        if (resource is null) return null;
        var identity = new WorkloadIdentityLabels(deployment.TenantId, deployment.ResourceGroupId,
            deployment.IsolationScopeId, deployment.RuntimeCellId, deployment.Placement.DeploymentTargetId,
            WebServiceResourceDeployment.KubernetesResourceTypeLabel, deployment.ResourceId, deployment.Id);
        var client = await clients.GetAsync(deployment.Placement.DeploymentTargetId, ct);
        return await KubernetesWorkloadDiagnostics.DeploymentAsync(client, resource.Name, deployment.Placement.KubernetesNamespace, identity, deployment.DesiredGeneration, ct);
    }

    public async Task<WorkloadDiagnostics> DiscoverAsync(ResourceDeploymentReconciliationSnapshot deployment, CancellationToken cancellationToken = default) =>
        await KubernetesWorkloadDiagnostics.DiscoverAsync(await ResolveDiagnosticsAsync(deployment, cancellationToken), cancellationToken);

    public Task<RuntimeLogOpenResult> OpenLogsAsync(ResourceDeploymentReconciliationSnapshot deployment, RuntimeLogSelection selection, CancellationToken cancellationToken = default) =>
        KubernetesWorkloadDiagnostics.OpenAsync(ct => ResolveDiagnosticsAsync(deployment, ct), selection, cancellationToken);

    public async Task<WorkloadObservation> ObserveAsync(ResourceDeploymentReconciliationSnapshot deployment, CancellationToken cancellationToken = default)
    {
        if (deployment.ResourceType != WebServiceResourceDeployment.ResourceType)
            return new UnavailableWorkloadObservation("ResourceTypeMismatch");
        var resource = await loadResource(deployment.TenantId, deployment.ResourceId, cancellationToken);
        if (resource is null) return new UnavailableWorkloadObservation("ResourceNotFound");
        var identity = new WorkloadIdentityLabels(deployment.TenantId, deployment.ResourceGroupId,
            deployment.IsolationScopeId, deployment.RuntimeCellId, deployment.Placement.DeploymentTargetId,
            WebServiceResourceDeployment.KubernetesResourceTypeLabel, deployment.ResourceId, deployment.Id);
        var client = await clients.GetAsync(deployment.Placement.DeploymentTargetId, cancellationToken);
        return await KubernetesWorkloadObservation.DeploymentAsync(client, resource.Name,
            deployment.Placement.KubernetesNamespace, identity, deployment.DesiredGeneration, cancellationToken);
    }
}
