using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Reconciliation;

internal sealed class MongoRetryExactResourceDeployment(
    IMongoCollection<ResourceDeployment> resourceDeployments,
    StoreOutboxWrites storeOutboxWrites,
    TimeProvider timeProvider)
{
    public async Task<Result<PlacementSummaryDto, ResourceDeploymentMutationError>> InvokeAsync(
        ResourceDeploymentExactRetryRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ResourceDeploymentPersistenceGuards.EnsureActiveTransaction(
            session,
            "Resource deployment mutations");
        ArgumentNullException.ThrowIfNull(request);
        var current = await resourceDeployments
            .Find(
                session,
                ResourceDeploymentFilters.ForExactDeployment(
                    request.TenantId,
                    request.ResourceType,
                    request.ResourceId,
                    request.ResourceDeploymentId))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
        {
            return ResourceDeploymentMutationSupport.Error<PlacementSummaryDto>(
                new ResourceDeploymentMutationNotFound(
                    request.ResourceId,
                    request.ResourceDeploymentId));
        }
        if (current.Revision != request.ExpectedDeploymentRevision)
        {
            return ResourceDeploymentMutationSupport.Error<PlacementSummaryDto>(
                new ResourceDeploymentChanged(request.ResourceId, request.ResourceDeploymentId));
        }
        if (current.State is not FailedResourceDeploymentState { Retryable: true } failed ||
            !ResourceDeploymentMutationSupport.Matches(
                request.ExpectedOperation,
                failed.Operation))
        {
            return ResourceDeploymentMutationSupport.Error<PlacementSummaryDto>(
                new ResourceDeploymentRetryUnavailable(
                    request.ResourceId,
                    request.ResourceDeploymentId));
        }

        var retried = current.Retry(timeProvider.GetUtcNow().UtcDateTime);
        var replacement = await resourceDeployments.ReplaceOneAsync(
            session,
            ResourceDeploymentFilters.ForRetryAtRevision(current),
            retried,
            cancellationToken: cancellationToken);
        if (replacement.ModifiedCount != 1)
        {
            return ResourceDeploymentMutationSupport.Error<PlacementSummaryDto>(
                new ResourceDeploymentChanged(
                    request.ResourceId,
                    request.ResourceDeploymentId));
        }

        await storeOutboxWrites(
            session,
            [retried.ToCurrentRequestWrite()],
            cancellationToken);
        return new Ok<PlacementSummaryDto>(retried.ToPlacementSummaryDto());
    }
}
