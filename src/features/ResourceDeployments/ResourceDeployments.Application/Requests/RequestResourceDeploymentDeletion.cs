using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application;
using Pole.Features.ResourceDeployments.Application.Admission;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Mongo;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Requests;

internal sealed class MongoRequestResourceDeploymentDeletion(
    IMongoCollection<ResourceDeployment> resourceDeployments,
    StoreOutboxWrites storeOutboxWrites,
    ResourceDeploymentConnectionGuard connectionGuard,
    TimeProvider timeProvider,
    ResourceDeploymentIsolationProjection isolation)
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
        if (!await connectionGuard.FenceAsync(
                request.TenantId, request.ResourceType, request.ResourceId, session, cancellationToken))
            return new ResourceDeploymentMutationError(new ResourceDeploymentMutationNotFound(request.ResourceId));

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
        var mutation = updated.Revision == current.Revision
            ? await isolation.ProjectAsync(current, cancellationToken)
            : await ResourceDeploymentMutationSupport.ReplaceAndEnqueueAsync(
                resourceDeployments,
                storeOutboxWrites,
                isolation,
                current,
                updated,
                session,
                cancellationToken);
        if (mutation is ResourceDeploymentMutationError)
            return mutation;
        var connectionConflict = await connectionGuard.ReconcileAsync(
            request.TenantId, request.ResourceType, request.ResourceId, session, cancellationToken);
        return connectionConflict is null
            ? mutation
            : new ResourceDeploymentMutationError(connectionConflict);
    }
}
