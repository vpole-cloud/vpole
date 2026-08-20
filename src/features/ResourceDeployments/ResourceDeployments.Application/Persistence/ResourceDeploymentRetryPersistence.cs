using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Endpoints;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Persistence;

internal sealed class ResourceDeploymentRetryPersistence(
    IMongoCollection<ResourceDeployment> resourceDeployments,
    StoreOutboxWrites storeOutboxWrites,
    TimeProvider timeProvider)
{
    public async Task<Result<ResourceDeploymentDto, RetryResourceDeploymentError>>
        RetryAsync(
            string tenantId,
            string deploymentId,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsInTransaction)
        {
            throw new InvalidOperationException(
                "Resource deployment retry must run inside the logical resource transaction.");
        }

        var filter = Builders<ResourceDeployment>.Filter.And(
            Builders<ResourceDeployment>.Filter.Eq(
                deployment => deployment.Id,
                deploymentId),
            Builders<ResourceDeployment>.Filter.ForTenant(tenantId));
        var current = await resourceDeployments
            .Find(session, filter)
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
        {
            return new Err<RetryResourceDeploymentError>(
                new RetryResourceDeploymentError(
                    new ResourceDeploymentNotFound(deploymentId)));
        }
        if (current.State is not FailedResourceDeploymentState
            {
                Retryable: true
            })
        {
            return new Err<RetryResourceDeploymentError>(
                new RetryResourceDeploymentError(
                    new ResourceDeploymentNotRetryable(deploymentId)));
        }

        var retried = current.Retry(timeProvider.GetUtcNow().UtcDateTime);
        var replacement = await resourceDeployments.ReplaceOneAsync(
            session,
            Builders<ResourceDeployment>.Filter.And(
                filter,
                Builders<ResourceDeployment>.Filter.Eq(
                    deployment => deployment.Revision,
                    current.Revision),
                Builders<ResourceDeployment>.Filter
                    .OfType<ResourceDeploymentState, FailedResourceDeploymentState>(
                        deployment => deployment.State)),
            retried,
            cancellationToken: cancellationToken);
        if (replacement.ModifiedCount != 1)
        {
            return new Err<RetryResourceDeploymentError>(
                new RetryResourceDeploymentError(
                    new ResourceDeploymentRetryConflict(deploymentId)));
        }

        await storeOutboxWrites(
            session,
            [retried.ToCurrentRequestWrite()],
            cancellationToken);
        return new Ok<ResourceDeploymentDto>(retried.ToDto());
    }
}
