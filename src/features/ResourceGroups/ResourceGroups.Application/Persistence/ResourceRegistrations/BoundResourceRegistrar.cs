using MongoDB.Driver;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Workloads;

namespace Pole.Features.ResourceGroups.Application.Persistence.ResourceRegistrations;

internal sealed class BoundResourceRegistrar(
    MongoResourceRegistrationMutations mutations,
    string? owningPluginId) : IResourceRegistrar
{
    public Task<ResourceRegistrationOutcome> RegisterAsync(
        RegisterResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) =>
        mutations.RegisterAsync(
            owningPluginId,
            request.TenantId,
            request.ResourceGroupId,
            request.Resource,
            session,
            cancellationToken);

    public Task<ResourceRegistrationOutcome> UpdateAsync(
        UpdateRegisteredResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) =>
        mutations.UpdateAsync(
            owningPluginId,
            request.TenantId,
            request.ResourceGroupId,
            request.Resource,
            session,
            cancellationToken);

    public Task<ResourceUnregistrationOutcome> UnregisterAsync(
        UnregisterResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) =>
        mutations.UnregisterAsync(
            owningPluginId,
            request.TenantId,
            request.ResourceGroupId,
            request.ResourceTypeId,
            request.ResourceId,
            session,
            cancellationToken);
}
