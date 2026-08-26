using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Requests;

internal sealed class MongoRequestResourceDeploymentSetDeletion(
    IMongoCollection<ResourceDeployment> resourceDeployments,
    StoreOutboxWrites storeOutboxWrites,
    TimeProvider timeProvider)
{
    public async Task<Result<
        IReadOnlyList<PlacementSummaryDto>,
        ResourceDeploymentMutationError>> InvokeAsync(
        ResourceDeploymentSetDeletionRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ResourceDeploymentPersistenceGuards.EnsureActiveTransaction(
            session,
            "Resource deployment mutations");
        ArgumentNullException.ThrowIfNull(request);
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
            return ResourceDeploymentMutationSupport.Error<IReadOnlyList<PlacementSummaryDto>>(
                new ResourceDeploymentMutationNotFound(request.ResourceId));
        }

        var byId = current.ToDictionary(deployment => deployment.Id, StringComparer.Ordinal);
        foreach (var item in request.Deployments)
        {
            var deployment = byId[item.ResourceDeploymentId];
            if (deployment.Revision != item.ExpectedDeploymentRevision)
            {
                return ResourceDeploymentMutationSupport.Error<IReadOnlyList<PlacementSummaryDto>>(
                    new ResourceDeploymentChanged(request.ResourceId, item.ResourceDeploymentId));
            }

            var computeExpectation = ResourceDeploymentMutationSupport.EnsureExpectedCompute(
                request.ResourceId,
                item.ExpectedCompute,
                deployment);
            if (computeExpectation is not null)
                return new Err<ResourceDeploymentMutationError>(computeExpectation.Value);
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
                return ResourceDeploymentMutationSupport.Error<IReadOnlyList<PlacementSummaryDto>>(
                    new ResourceDeploymentChanged(request.ResourceId, original.Id));
            }
        }

        await storeOutboxWrites(
            session,
            changed
                .Where(deployment => deployment.State is PendingResourceDeploymentState)
                .Select(deployment => deployment.ToCurrentRequestWrite())
                .ToArray(),
            cancellationToken);

        return new Ok<IReadOnlyList<PlacementSummaryDto>>(
            updated.Select(deployment => deployment.ToPlacementSummaryDto()).ToArray());
    }
}
