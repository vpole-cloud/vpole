using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application;
using Pole.Features.ResourceDeployments.Application.Admission;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Mongo;
using Pole.Features.ResourceDeployments.Application.Planning;
using Pole.Features.ResourceDeployments.Application.Queries;
using Pole.Features.ResourceDeployments.Application.Reconciliation;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Application.Placements;

internal sealed class MongoLoadResourceDeploymentAccessPlacement(
    IMongoCollection<ResourceDeployment> resourceDeployments)
{
    public async Task<ResourceDeploymentAccessPlacement?> InvokeAsync(
        LoadExactResourceDeploymentAccessPlacementRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Access placement lookup requires a Mongo transaction.");

        var deployment = await resourceDeployments.FindOneAndUpdateAsync(
            session,
            ResourceDeploymentFilters.ForExactDeployment(
                request.TenantId, request.ResourceType, request.ResourceId, request.DeploymentId),
            Builders<ResourceDeployment>.Update.Inc(item => item.ReferenceFence, 1),
            new FindOneAndUpdateOptions<ResourceDeployment> { ReturnDocument = ReturnDocument.After },
            cancellationToken);
        if (deployment is null)
            return null;

        var status = deployment.State switch
        {
            PreparedResourceDeploymentState => ResourceDeploymentStatus.Prepared,
            WaitingForRuntimeCellResourceDeploymentState => ResourceDeploymentStatus.WaitingForRuntimeCell,
            PendingResourceDeploymentState => ResourceDeploymentStatus.Pending,
            ReconcilingResourceDeploymentState => ResourceDeploymentStatus.Reconciling,
            AppliedResourceDeploymentState => ResourceDeploymentStatus.Applied,
            FailedResourceDeploymentState => ResourceDeploymentStatus.Failed,
            DeletedResourceDeploymentState => ResourceDeploymentStatus.Deleted,
            _ => throw new InvalidOperationException(
                "Unexpected resource deployment state during private access placement lookup.")
        };
        return new ResourceDeploymentAccessPlacement(
            deployment.TenantId,
            deployment.ResourceType,
            deployment.ResourceId,
            deployment.ResourceGroupId,
            deployment.Id,
            deployment.ResolvedPlacement.DeploymentTargetId,
            deployment.RuntimeCellId,
            deployment.IsolationScopeId,
            deployment.ResolvedPlacement.KubernetesNamespace,
            deployment.Revision,
            deployment.DesiredGeneration,
            status,
            deployment.DesiredOperation.ToContractOperation());
    }
}
