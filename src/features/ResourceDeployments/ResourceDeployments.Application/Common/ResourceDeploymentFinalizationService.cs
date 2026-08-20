using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Persistence;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Common;

internal sealed class ResourceDeploymentFinalizationService(
    IMongoCollection<ResourceDeployment> resourceDeployments)
{
    public async Task<ResourceDeploymentFinalizationReadiness> GetReadinessAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        var states = await resourceDeployments
            .Find(
                session,
                ResourceDeploymentFilters.ForResource(
                    tenantId,
                    resourceType,
                    resourceId))
            .Project(deployment => deployment.State)
            .ToListAsync(cancellationToken);
        if (states.Count == 0)
            return ResourceDeploymentFinalizationReadiness.Missing;

        return states.All(state => state is DeletedResourceDeploymentState)
            ? ResourceDeploymentFinalizationReadiness.Ready
            : ResourceDeploymentFinalizationReadiness.Waiting;
    }

    public async Task<Result<Unit, ResourceDeploymentFinalizationConflict>>
        PurgeDeletedAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        var filter = ResourceDeploymentFilters.ForResource(
            tenantId,
            resourceType,
            resourceId);
        var states = await resourceDeployments
            .Find(session, filter)
            .Project(deployment => deployment.State)
            .ToListAsync(cancellationToken);
        if (states.Count == 0 ||
            states.Any(state => state is not DeletedResourceDeploymentState))
        {
            return new Err<ResourceDeploymentFinalizationConflict>(
                new ResourceDeploymentFinalizationConflict(resourceId));
        }

        var deletion = await resourceDeployments.DeleteManyAsync(
            session,
            filter,
            cancellationToken: cancellationToken);
        return deletion.DeletedCount == states.Count
            ? new Ok<Unit>(Unit.Value)
            : new Err<ResourceDeploymentFinalizationConflict>(
                new ResourceDeploymentFinalizationConflict(resourceId));
    }

    public async Task<ResourceDeploymentFinalizationReadiness> GetExactReadinessAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        string resourceDeploymentId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        var state = await resourceDeployments
            .Find(
                session,
                ResourceDeploymentFilters.ForExactDeployment(
                    tenantId,
                    resourceType,
                    resourceId,
                    resourceDeploymentId))
            .Project(deployment => deployment.State)
            .FirstOrDefaultAsync(cancellationToken);
        return state switch
        {
            null => ResourceDeploymentFinalizationReadiness.Missing,
            DeletedResourceDeploymentState => ResourceDeploymentFinalizationReadiness.Ready,
            _ => ResourceDeploymentFinalizationReadiness.Waiting
        };
    }

    public async Task<Result<Unit, ResourceDeploymentFinalizationConflict>>
        PurgeExactDeletedAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        string resourceDeploymentId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        var filter = ResourceDeploymentFilters.ForExactDeployment(
            tenantId,
            resourceType,
            resourceId,
            resourceDeploymentId);
        var deployment = await resourceDeployments
            .Find(session, filter)
            .FirstOrDefaultAsync(cancellationToken);
        if (deployment is null || deployment.State is not DeletedResourceDeploymentState)
        {
            return new Err<ResourceDeploymentFinalizationConflict>(
                new ResourceDeploymentFinalizationConflict(
                    resourceId,
                    resourceDeploymentId));
        }

        var deletion = await resourceDeployments.DeleteManyAsync(
            session,
            ResourceDeploymentFilters.ForExactDeploymentAtRevision(
                tenantId,
                resourceType,
                resourceId,
                resourceDeploymentId,
                deployment.Revision),
            cancellationToken: cancellationToken);
        return deletion.DeletedCount == 1
            ? new Ok<Unit>(Unit.Value)
            : new Err<ResourceDeploymentFinalizationConflict>(
                new ResourceDeploymentFinalizationConflict(
                    resourceId,
                    resourceDeploymentId));
    }

    private static void EnsureTransaction(IClientSessionHandle session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsInTransaction)
        {
            throw new InvalidOperationException(
                "Resource deployment finalization must run inside the logical resource transaction.");
        }
    }
}
