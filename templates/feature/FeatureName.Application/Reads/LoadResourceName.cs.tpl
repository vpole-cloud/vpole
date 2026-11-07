using MongoDB.Driver;
using Pole.Features.FeatureName.Application.Mongo;
using Pole.Features.FeatureName.Domain;

namespace Pole.Features.FeatureName.Application.Reads;

public sealed record ResourceNameIdentity(
    string TenantId,
    string Id);

public delegate Task<ResourceName?> LoadResourceName(
    ResourceNameIdentity identity,
    CancellationToken cancellationToken);

internal sealed class MongoLoadResourceName(IMongoCollection<ResourceName> resources)
{
    public async Task<ResourceName?> InvokeAsync(ResourceNameIdentity identity, CancellationToken ct) =>
        await resources.Find(ResourceNameFilters.ById(identity.TenantId, identity.Id)).FirstOrDefaultAsync(ct);
}
