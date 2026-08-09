using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Persistence;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Common;

public sealed class ResourceDeploymentFinalizationService(
    IResourceDeploymentDbContext dbContext)
    : IResourceDeploymentFinalizationService
{
    public async Task<ResourceDeploymentFinalizationReadiness> GetReadinessAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        var states = await dbContext.ResourceDeployments
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

    public async Task<Result> PurgeDeletedAsync(
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
        var states = await dbContext.ResourceDeployments
            .Find(session, filter)
            .Project(deployment => deployment.State)
            .ToListAsync(cancellationToken);
        if (states.Count == 0 ||
            states.Any(state => state is not DeletedResourceDeploymentState))
        {
            return Failures.Conflict(
                "The resource deployment is not ready for finalization.");
        }

        var deletion = await dbContext.ResourceDeployments.DeleteManyAsync(
            session,
            filter,
            cancellationToken: cancellationToken);
        return deletion.DeletedCount == states.Count
            ? Result.Success()
            : Failures.Conflict(
                "The resource deployment changed while finalization was being completed.");
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
