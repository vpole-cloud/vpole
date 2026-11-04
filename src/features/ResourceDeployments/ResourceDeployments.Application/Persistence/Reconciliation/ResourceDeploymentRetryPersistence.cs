using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Endpoints;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Reconciliation;

internal sealed class ResourceDeploymentRetryPersistence(
    IMongoCollection<ResourceDeployment> resourceDeployments,
    StoreOutboxWrites storeOutboxWrites,
    TimeProvider timeProvider,
    ResourceDeploymentIsolationProjection isolation)
{
    public async Task<RetryResourceDeploymentOutcome>
        RetryAsync(
            string tenantId,
            string deploymentId,
            long expectedRevision,
            ResourceDeploymentOperation expectedOperation,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsInTransaction)
        {
            throw new InvalidOperationException(
                "Resource deployment retry must run inside the logical resource transaction.");
        }

        var current = await resourceDeployments
            .Find(
                session,
                ResourceDeploymentFilters.ForTenantDeployment(tenantId, deploymentId))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
        {
            return new RetryResourceDeploymentError(
                new ResourceDeploymentNotFound(deploymentId));
        }
        if (current.Revision != expectedRevision || current.DesiredOperation != expectedOperation)
            return new RetryResourceDeploymentError(new ResourceDeploymentRetryConflict(deploymentId));
        if (current.State is not FailedResourceDeploymentState
            {
                Retryable: true
            })
        {
            return new RetryResourceDeploymentError(
                new ResourceDeploymentNotRetryable(deploymentId));
        }

        var retried = current.Retry(timeProvider.GetUtcNow().UtcDateTime);
        var replacement = await resourceDeployments.ReplaceOneAsync(
            session,
            ResourceDeploymentFilters.ForRetryAtRevision(current),
            retried,
            cancellationToken: cancellationToken);
        if (replacement.ModifiedCount != 1)
        {
            return new RetryResourceDeploymentError(
                new ResourceDeploymentRetryConflict(deploymentId));
        }

        await storeOutboxWrites(
            session,
            [retried.ToCurrentRequestWrite()],
            cancellationToken);
        return await isolation.ProjectDtoAsync(retried, cancellationToken);
    }
}
