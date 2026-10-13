using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Persistence.Reads;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Observation;

public sealed class ResourceDeploymentObservationContextLoader(
    ResourceDeploymentOperationsPersistence persistence,
    ResourceDeploymentObserverCatalog catalog,
    LookupDeploymentTarget lookupTarget)
{
    public async Task<ResourceDeploymentObservationContext?> LoadWatchAsync(ObservationIdentity identity, CancellationToken ct)
    {
        var deployment = await persistence.LoadAsync(identity.TenantId, identity.DeploymentId, ct);
        if (deployment is null || deployment.DesiredOperation != ResourceDeploymentOperation.Apply ||
            deployment.State is DeletedResourceDeploymentState || !SupportsWatch(deployment.ResourceType)) return null;
        return await FromAsync(deployment, ct);
    }

    public bool SupportsWatch(string resourceType) => catalog.Find(resourceType) is { ChangeSourceType: not null, BoundObserverType: not null };

    public async Task<ResourceDeploymentObservationContext?> FromAsync(ResourceDeployment deployment, CancellationToken ct)
    {
        var target = await lookupTarget(deployment.ResolvedPlacement.DeploymentTargetId, ct);
        return target is DeploymentTargetLookupDto { Enabled: true, DeletionRequested: false, ConnectionVersion: > 0 } current
            ? new(deployment.ToReconciliationSnapshot(), deployment.Revision, current.ConnectionVersion)
            : null;
    }

    public async Task<bool> IsCurrentAsync(ResourceDeployment deployment, ResourceDeploymentObservationContext? context, CancellationToken ct)
    {
        var current = await persistence.LoadAsync(deployment.TenantId, deployment.Id, ct);
        if (current is null || current.Revision != deployment.Revision || current.DesiredGeneration != deployment.DesiredGeneration ||
            current.ResourceType != deployment.ResourceType || current.ResourceId != deployment.ResourceId ||
            current.ResourceGroupId != deployment.ResourceGroupId || current.IsolationScopeId != deployment.IsolationScopeId ||
            current.RuntimeCellId != deployment.RuntimeCellId || current.ResolvedPlacement.DeploymentTargetId != deployment.ResolvedPlacement.DeploymentTargetId ||
            current.ResolvedPlacement.KubernetesNamespace != deployment.ResolvedPlacement.KubernetesNamespace) return false;
        return context is null || await lookupTarget(context.Deployment.Placement.DeploymentTargetId, ct) is
            DeploymentTargetLookupDto { Enabled: true, DeletionRequested: false } target && target.ConnectionVersion == context.TargetConnectionVersion;
    }
}
