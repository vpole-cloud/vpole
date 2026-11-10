using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Mongo;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Requests;

internal sealed class MongoAdvanceResourceDeploymentRevision(
    IMongoCollection<ResourceDeployment> resourceDeployments,
    TimeProvider timeProvider,
    ResourceDeploymentIsolationProjection isolation
)
{
    public async Task<AdvanceResourceDeploymentRevisionOutcome> InvokeAsync(
        ResourceDeploymentResourceRevisionRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ResourceDeploymentPersistenceGuards.EnsureActiveTransaction(session, "Resource deployment mutations");
        ArgumentNullException.ThrowIfNull(request);
        var current = await resourceDeployments
            .Find(
                session,
                ResourceDeploymentFilters.ForResource(request.TenantId, request.ResourceType, request.ResourceId))
            .SortBy(deployment => deployment.Id)
            .ToListAsync(cancellationToken);
        if (current.Count == 0)
            return new AdvanceResourceDeploymentRevisionError(new ResourceDeploymentsNotFound(request.ResourceId));

        var active = current.Where(deployment => deployment.State is not DeletedResourceDeploymentState).ToArray();
        if (active.Length == 0)
            return new AdvanceResourceDeploymentRevisionError(
                new ResourceDeploymentsHaveNoActivePlacement(request.ResourceId));

        var updatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var results = new List<PlacementSummaryDto>(active.Length);
        foreach (var deployment in active)
        {
            var updated = deployment.AdvanceResourceRevision(request.ResourceRevision, updatedAtUtc);
            if (updated.Revision != deployment.Revision)
            {
                var replacement = await resourceDeployments.ReplaceOneAsync(
                    session,
                    ResourceDeploymentFilters.AtRevision(deployment),
                    updated,
                    cancellationToken: cancellationToken);
                if (replacement.ModifiedCount != 1)
                    return new AdvanceResourceDeploymentRevisionError(
                        new ResourceDeploymentRevisionConflict(request.ResourceId));
            }

            results.Add(await isolation.ProjectAsync(updated, cancellationToken));
        }

        return new MutatedResourceDeployments(results);
    }
}
