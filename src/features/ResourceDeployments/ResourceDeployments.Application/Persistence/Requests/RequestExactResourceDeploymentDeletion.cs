using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Requests;

internal sealed class MongoRequestExactResourceDeploymentDeletion(
    IMongoCollection<ResourceDeployment> resourceDeployments,
    StoreOutboxWrites storeOutboxWrites,
    TimeProvider timeProvider)
{
    public async Task<ResourceDeploymentMutationOutcome> InvokeAsync(
        ResourceDeploymentExactMutationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ResourceDeploymentPersistenceGuards.EnsureActiveTransaction(
            session,
            "Resource deployment mutations");
        ArgumentNullException.ThrowIfNull(request);
        var loaded = await ResourceDeploymentMutationSupport.LoadExactAsync(
            resourceDeployments,
            request,
            session,
            cancellationToken);
        if (loaded is ResourceDeploymentMutationError loadFailure)
            return loadFailure;
        var current = (ResourceDeployment)loaded.Value;
        if (current.Revision != request.ExpectedDeploymentRevision)
        {
            return new ResourceDeploymentMutationError(
                new ResourceDeploymentChanged(request.ResourceId, request.ResourceDeploymentId));
        }

        var computeExpectation = ResourceDeploymentMutationSupport.EnsureExpectedCompute(
            request.ResourceId,
            request.ExpectedCompute,
            current);
        if (computeExpectation is not null)
            return computeExpectation.Value;
        if (request.ReplacementCompute is not null ||
            request.ReplacementIngress is not null ||
            request.FixedReplicas is not null)
        {
            throw new ArgumentException(
                "Compute, ingress, and replica updates are not valid for resource deployment deletion.",
                nameof(request));
        }

        var updated = current.RequestDeletion(
            timeProvider.GetUtcNow().UtcDateTime,
            request.ResourceRevision);
        return updated.Revision == current.Revision
            ? current.ToPlacementSummaryDto()
            : await ResourceDeploymentMutationSupport.ReplaceAndEnqueueAsync(
                resourceDeployments,
                storeOutboxWrites,
                current,
                updated,
                session,
                cancellationToken);
    }
}
