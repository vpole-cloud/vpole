using Pole.Features.ResourceGroups.Application.References;
using Pole.Features.ResourceGroups.Application.Mongo;
using MongoDB.Driver;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Workloads;

namespace Pole.Features.ResourceGroups.Application.ResourceRegistrations;

internal sealed class MongoResourceRegistrationConnectionFence(
    IMongoCollection<ResourceRegistration> registrations)
{
    public async Task<RegisteredResource?> InvokeAsync(
        FenceRegisteredResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("A resource reference fence requires an active transaction.");

        var registration = await registrations.FindOneAndUpdateAsync(
            session,
            ResourceRegistrationFilters.ByIdentity(request.TenantId, request.ResourceTypeId, request.ResourceId),
            Builders<ResourceRegistration>.Update.Inc(item => item.ReferenceFence, 1),
            new FindOneAndUpdateOptions<ResourceRegistration>
            {
                ReturnDocument = ReturnDocument.After
            },
            cancellationToken);
        return registration is null
            ? null
            : new RegisteredResource(
                registration.Id,
                registration.TenantId,
                registration.ResourceGroupId,
                ResourceTypeId.Create(registration.ResourceTypeId),
                registration.ResourceId,
                registration.DisplayName,
                registration.OwningPluginId,
                registration.CreatedAtUtc,
                registration.UpdatedAtUtc);
    }
}
