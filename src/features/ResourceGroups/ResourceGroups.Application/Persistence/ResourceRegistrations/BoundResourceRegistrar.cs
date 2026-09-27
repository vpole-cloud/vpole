using MongoDB.Driver;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Workloads;

namespace Pole.Features.ResourceGroups.Application.Persistence.ResourceRegistrations;

internal sealed class BoundResourceRegistrar(
    MongoResourceRegistrationMutations mutations,
    string? owningPluginId) : IResourceRegistrar
{
    public Task<ResourceRegistrationOutcome> RegisterAsync(
        string tenantId,
        string resourceGroupId,
        ResourceRegistrationDescriptor resource,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) =>
        mutations.RegisterAsync(
            owningPluginId,
            tenantId,
            resourceGroupId,
            resource,
            session,
            cancellationToken);

    public Task<ResourceRegistrationOutcome> UpdateAsync(
        string tenantId,
        string resourceGroupId,
        ResourceRegistrationDescriptor resource,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) =>
        mutations.UpdateAsync(
            owningPluginId,
            tenantId,
            resourceGroupId,
            resource,
            session,
            cancellationToken);

    public Task<ResourceUnregistrationOutcome> UnregisterAsync(
        string tenantId,
        string resourceGroupId,
        ResourceTypeId resourceTypeId,
        string resourceId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) =>
        mutations.UnregisterAsync(
            owningPluginId,
            tenantId,
            resourceGroupId,
            resourceTypeId,
            resourceId,
            session,
            cancellationToken);
}
