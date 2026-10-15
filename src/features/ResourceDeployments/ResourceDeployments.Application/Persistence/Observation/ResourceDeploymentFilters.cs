using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Persistence;

internal static partial class ResourceDeploymentFilters
{
    internal static FilterDefinition<ResourceDeployment> ForCurrentObservation(
        ResourceDeploymentObservationContext context)
    {
        var deployment = context.Deployment;
        return ForExactDeploymentAtRevision(deployment.TenantId,
                   deployment.ResourceType,
                   deployment.ResourceId,
                   deployment.Id,
                   context.DeploymentRevision) &
               Builders<ResourceDeployment>.Filter.Eq(item => item.DesiredGeneration, deployment.DesiredGeneration) &
               Builders<ResourceDeployment>.Filter.Eq(item => item.ResourceRevision, deployment.ResourceRevision) &
               Builders<ResourceDeployment>.Filter.Eq(item => item.FixedReplicas, deployment.FixedReplicas) &
               Builders<ResourceDeployment>.Filter.Eq(item => item.ResourceGroupId, deployment.ResourceGroupId) &
               Builders<ResourceDeployment>.Filter.Eq(item => item.IsolationScopeId, deployment.IsolationScopeId) &
               Builders<ResourceDeployment>.Filter.Eq(item => item.RuntimeCellId, deployment.RuntimeCellId) &
               Builders<ResourceDeployment>.Filter.Eq(item => item.ResolvedPlacement.DeploymentTargetId,
                   deployment.Placement.DeploymentTargetId) &
               Builders<ResourceDeployment>.Filter.Eq(item => item.ResolvedPlacement.KubernetesNamespace,
                   deployment.Placement.KubernetesNamespace) &
               Builders<ResourceDeployment>.Filter.Eq(item =>
                       item.ResolvedPlacement.TargetWorkloadConfigurationRevision,
                   deployment.Placement.TargetWorkloadConfigurationRevision) &
               Builders<ResourceDeployment>.Filter.Eq(item => item.ResolvedPlacement.Compute,
                   deployment.Placement.Compute.ToDomain()) &
               Builders<ResourceDeployment>.Filter.Eq(item => item.ResolvedPlacement.Ingress,
                   deployment.Placement.Ingress.ToDomain()) &
               Builders<ResourceDeployment>.Filter.Eq(item => item.ResolvedPlacement.Storage,
                   deployment.Placement.Storage.ToDomain()) &
               Builders<ResourceDeployment>.Filter.Not(Builders<ResourceDeployment>.Filter
                   .OfType<ResourceDeploymentState, DeletedResourceDeploymentState>(item => item.State)) &
               Builders<ResourceDeployment>.Filter.Lt(item => item.ReferenceFence, long.MaxValue);
    }
}
