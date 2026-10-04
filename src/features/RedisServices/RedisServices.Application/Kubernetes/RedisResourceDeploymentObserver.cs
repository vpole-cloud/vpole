using Pole.Features.Core.Kubernetes;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Persistence.Reads;
using Pole.Workloads;

namespace Pole.Features.RedisServices.Application.Kubernetes;

public sealed class RedisResourceDeploymentObserver(
    LoadRedisById loadResource,
    IKubernetesClientProvider clients) : IResourceDeploymentObserver
{
    public async Task<WorkloadObservation> ObserveAsync(ResourceDeploymentReconciliationSnapshot deployment, CancellationToken cancellationToken = default)
    {
        if (deployment.ResourceType != RedisResourceDeployment.ResourceType)
            return new UnavailableWorkloadObservation("ResourceTypeMismatch");
        var resource = await loadResource(deployment.TenantId, deployment.ResourceId, cancellationToken);
        if (resource is null) return new UnavailableWorkloadObservation("ResourceNotFound");
        var identity = new WorkloadIdentityLabels(deployment.TenantId, deployment.ResourceGroupId,
            deployment.IsolationScopeId, deployment.RuntimeCellId, deployment.Placement.DeploymentTargetId,
            RedisResourceDeployment.KubernetesResourceTypeLabel, deployment.ResourceId, deployment.Id);
        var client = await clients.GetAsync(deployment.Placement.DeploymentTargetId, cancellationToken);
        return await KubernetesWorkloadObservation.StatefulSetAsync(client, resource.Name,
            deployment.Placement.KubernetesNamespace, identity, deployment.DesiredGeneration,
            KubernetesWorkloadObservation.TemplateGenerationAnnotation, false, cancellationToken);
    }
}
