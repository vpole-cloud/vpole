using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Requests;

internal sealed class MongoRequestResourceDeploymentSetDeletion(
    IMongoCollection<ResourceDeployment> resourceDeployments,
    StoreOutboxWrites storeOutboxWrites,
    ResourceDeploymentConnectionGuard connectionGuard,
    TimeProvider timeProvider,
    ResourceDeploymentIsolationProjection isolation)
{
    public async Task<ResourceDeploymentSetMutationOutcome> InvokeAsync(
        ResourceDeploymentSetDeletionRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ResourceDeploymentPersistenceGuards.EnsureActiveTransaction(
            session,
            "Resource deployment mutations");
        ArgumentNullException.ThrowIfNull(request);
        if (!await connectionGuard.FenceAsync(
                request.TenantId, request.ResourceType, request.ResourceId, session, cancellationToken))
            return new ResourceDeploymentMutationError(new ResourceDeploymentMutationNotFound(request.ResourceId));

        ResourceDeploymentMutationSupport.EnsureSetIsNonEmptyAndUnique(
            request.Deployments,
            deployment => deployment.ResourceDeploymentId,
            nameof(request));

        var deploymentIds = request.Deployments
            .Select(item => item.ResourceDeploymentId)
            .ToArray();
        var current = await resourceDeployments
            .Find(
                session,
                ResourceDeploymentFilters.ForResourceDeploymentIds(
                    request.TenantId,
                    request.ResourceType,
                    request.ResourceId,
                    deploymentIds))
            .ToListAsync(cancellationToken);
        if (current.Count != request.Deployments.Count)
        {
            return new ResourceDeploymentMutationError(
                new ResourceDeploymentMutationNotFound(request.ResourceId));
        }

        var byId = current.ToDictionary(deployment => deployment.Id, StringComparer.Ordinal);
        foreach (var item in request.Deployments)
        {
            var deployment = byId[item.ResourceDeploymentId];
            if (deployment.Revision != item.ExpectedDeploymentRevision)
            {
                return new ResourceDeploymentMutationError(
                    new ResourceDeploymentChanged(request.ResourceId, item.ResourceDeploymentId));
            }

            var computeExpectation = ResourceDeploymentMutationSupport.EnsureExpectedCompute(
                request.ResourceId,
                item.ExpectedCompute,
                deployment);
            if (computeExpectation is not null)
                return computeExpectation.Value;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var updated = request.Deployments
            .Select(item => byId[item.ResourceDeploymentId].RequestDeletion(
                now,
                request.ResourceRevision))
            .ToArray();
        var changed = updated
            .Where(deployment => deployment.Revision != byId[deployment.Id].Revision)
            .ToArray();
        foreach (var deployment in changed)
        {
            var original = byId[deployment.Id];
            var replacement = await resourceDeployments.ReplaceOneAsync(
                session,
                ResourceDeploymentFilters.AtRevision(original),
                deployment,
                cancellationToken: cancellationToken);
            if (replacement.ModifiedCount != 1)
            {
                return new ResourceDeploymentMutationError(
                    new ResourceDeploymentChanged(request.ResourceId, original.Id));
            }
        }

        var connectionConflict = await connectionGuard.ReconcileAsync(
            request.TenantId, request.ResourceType, request.ResourceId, session, cancellationToken);
        if (connectionConflict is not null)
            return new ResourceDeploymentMutationError(connectionConflict);

        await storeOutboxWrites(
            session,
            changed
                .Where(deployment => deployment.State is PendingResourceDeploymentState)
                .Select(deployment => deployment.ToCurrentRequestWrite())
                .ToArray(),
            cancellationToken);

        return new MutatedResourceDeployments(
            await isolation.ProjectAsync(updated, cancellationToken));
    }
}
