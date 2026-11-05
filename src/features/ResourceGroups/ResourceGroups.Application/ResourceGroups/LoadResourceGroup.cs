using MongoDB.Driver;
using Pole.Features.ResourceGroups.Application.Mongo;
using Pole.Features.ResourceGroups.Application.References;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.ResourceGroups;

public delegate Task<ResourceGroup?> LoadResourceGroup(
    string tenantId,
    string resourceGroupId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadResourceGroup(IMongoCollection<ResourceGroup> resourceGroups)
{
    public async Task<ResourceGroup?> InvokeAsync(
        string tenantId,
        string resourceGroupId,
        CancellationToken cancellationToken = default) =>
        await resourceGroups
            .Find(ResourceGroupFilters.ByTenantAndId(tenantId, resourceGroupId))
            .FirstOrDefaultAsync(cancellationToken);
}
