using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application;
using Pole.Features.ResourceDeployments.Application.Admission;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Mongo;
using Pole.Features.ResourceDeployments.Application.Placements;
using Pole.Features.ResourceDeployments.Application.Queries;
using Pole.Features.ResourceDeployments.Application.Reconciliation;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Contracts;

namespace Pole.Features.ResourceDeployments.Application.Planning;

internal sealed class MongoResourceDeploymentPlanningSnapshots(
    IMongoCollection<ResourceDeployment> deployments,
    FenceRegisteredResource fenceRegistration)
{
    public async Task<LoadResourceDeploymentPlanningSnapshotOutcome> ReadAsync(
        ResourceDeploymentPlanningRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        RequireTransaction(session);
        var current = await deployments.Find(session, ResourceDeploymentFilters.ForResource(
                request.TenantId, request.ResourceType, request.ResourceId))
            .SortBy(item => item.Id).ToListAsync(ct);
        if (current.Count == 0)
            return new ResourceDeploymentPlanningUnavailable();

        return new ResourceDeploymentPlanningSnapshot(
            request.TenantId,
            request.ResourceType,
            request.ResourceId,
            current.Select(ToPlanningItem).ToArray());
    }

    public async Task<FenceResourceDeploymentPlanningSnapshotOutcome> FenceAsync(
        ResourceDeploymentPlanningSnapshot request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        RequireTransaction(session);
        // All supported deployment-set writers take this same owner fence. It closes the
        // insertion phantom as well as the per-document update/delete races below.
        var registration = await fenceRegistration(new(request.TenantId, request.ResourceType, request.ResourceId),
            session, ct);
        if (registration is null || request.Items.Count == 0)
            return new ResourceDeploymentPlanningFenceRejected();

        var read = await ReadAsync(new(request.TenantId, request.ResourceType, request.ResourceId), session, ct);
        if (read is not ResourceDeploymentPlanningSnapshot current || current.Items.Count != request.Items.Count ||
            current.Items.Any(item => item.ResourceGroupId != registration.ResourceGroupId) ||
            !current.Items.Select(item => (item.DeploymentId, item.Revision)).SequenceEqual(
                request.Items.OrderBy(item => item.DeploymentId, StringComparer.Ordinal)
                    .Select(item => (item.DeploymentId, item.Revision))))
            return new ResourceDeploymentPlanningFenceRejected();

        foreach (var item in current.Items)
        {
            var result = await deployments.UpdateOneAsync(session,
                ResourceDeploymentFilters.ForExactDeploymentAtRevision(request.TenantId, request.ResourceType,
                    request.ResourceId, item.DeploymentId, item.Revision),
                Builders<ResourceDeployment>.Update.Inc(value => value.ReferenceFence, 1), cancellationToken: ct);
            if (result.ModifiedCount != 1)
                return new ResourceDeploymentPlanningFenceRejected();
        }

        return new ResourceDeploymentPlanningFenced();
    }

    private static ResourceDeploymentPlanningItem ToPlanningItem(ResourceDeployment deployment)
    {
        var state = deployment.DesiredOperation == ResourceDeploymentOperation.Delete ||
            deployment.State is DeletedResourceDeploymentState
                ? ResourceDeploymentPlanningState.Deleting
                : deployment.State is PreparedResourceDeploymentState
                    ? ResourceDeploymentPlanningState.Prepared
                    : ResourceDeploymentPlanningState.Active;

        var placement = deployment.ToReconciliationSnapshot().Placement;
        return new ResourceDeploymentPlanningItem(
            deployment.Id,
            deployment.ResourceGroupId,
            deployment.IsolationScopeId,
            deployment.ResolvedPlacement.DeploymentTargetId,
            deployment.Revision,
            deployment.FixedReplicas,
            placement.Compute,
            state,
            placement.Storage,
            placement.Ingress,
            placement.TargetWorkloadConfigurationRevision,
            deployment.DesiredGeneration,
            deployment.ResourceRevision);
    }

    private static void RequireTransaction(IClientSessionHandle session)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Planning snapshots require an active transaction.");
    }
}
