using MongoDB.Driver;
using Pole.Features.ResourceGroups.Application.Mongo;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Workloads;

namespace Pole.Features.ResourceGroups.Application.ResourceRegistrations;

internal sealed class MongoResourceRegistrationPlanning(
    IMongoCollection<ResourceRegistration> registrations
)
{
    public async Task<ResourceRegistrationPlanningSnapshot?> LoadAsync(
        LoadRegisteredResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = await registrations.Find(
                session,
                ResourceRegistrationFilters.ByIdentity(request.TenantId, request.ResourceTypeId, request.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        return current is null
            ? null
            : new ResourceRegistrationPlanningSnapshot(
                new RegisteredResource(
                    current.Id,
                    current.TenantId,
                    current.ResourceGroupId,
                    ResourceTypeId.Create(current.ResourceTypeId),
                    current.ResourceId,
                    current.DisplayName,
                    current.OwningPluginId,
                    current.CreatedAtUtc,
                    current.UpdatedAtUtc),
                current.ReferenceFence);
    }

    public async Task<FenceResourceRegistrationSnapshotOutcome> FenceAsync(
        ResourceRegistrationPlanningSnapshot snapshot,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Registration planning fences require a transaction.");

        var resource = snapshot.Resource;
        var filter = ResourceRegistrationFilters.AtPlanningSnapshot(snapshot);
        var result = await registrations.UpdateOneAsync(
            session,
            filter,
            Builders<ResourceRegistration>.Update.Inc(item => item.ReferenceFence, 1),
            cancellationToken: cancellationToken);
        return result.ModifiedCount == 1
            ? new ResourceRegistrationSnapshotFenced()
            : new ResourceRegistrationSnapshotChanged();
    }
}
