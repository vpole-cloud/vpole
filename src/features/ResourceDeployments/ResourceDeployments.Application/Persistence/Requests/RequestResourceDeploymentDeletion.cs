using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Requests;

internal sealed class MongoRequestResourceDeploymentDeletion(
    IMongoCollection<ResourceDeployment> resourceDeployments,
    StoreOutboxWrites storeOutboxWrites,
    TimeProvider timeProvider)
{
    public async Task<ResourceDeploymentMutationOutcome> InvokeAsync(
        ResourceDeploymentMutationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ResourceDeploymentPersistenceGuards.EnsureActiveTransaction(
            session,
            "Resource deployment mutations");
        ArgumentNullException.ThrowIfNull(request);
        var loaded = await ResourceDeploymentMutationSupport.LoadSingleAsync(
            resourceDeployments,
            request,
            session,
            cancellationToken);
        if (loaded is ResourceDeploymentMutationError loadFailure)
            return loadFailure;
        var current = (ResourceDeployment)loaded.Value;
        var computeExpectation = ResourceDeploymentMutationSupport.EnsureExpectedCompute(
            request.ResourceId,
            request.ExpectedCompute,
            current);
        if (computeExpectation is not null)
            return computeExpectation.Value;
        if (request.ReplacementCompute is not null)
        {
            throw new ArgumentException(
                "Compute profile adoption is not valid for resource deletion.",
                nameof(request));
        }

        var updated = current.RequestDeletion(timeProvider.GetUtcNow().UtcDateTime);
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
