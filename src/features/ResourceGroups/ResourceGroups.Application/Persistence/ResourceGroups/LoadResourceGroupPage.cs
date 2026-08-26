using MongoDB.Driver;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.Persistence.ResourceGroups;

public sealed record ResourceGroupPageQuery(
    string TenantId,
    int PageNumber,
    int PageSize,
    string? Search);

public sealed record ResourceGroupPage(
    IReadOnlyList<ResourceGroup> Items,
    long TotalCount);

public delegate Task<ResourceGroupPage> LoadResourceGroupPage(
    ResourceGroupPageQuery query,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadResourceGroupPage(
    IMongoCollection<ResourceGroup> resourceGroups)
{
    public async Task<ResourceGroupPage> InvokeAsync(
        ResourceGroupPageQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var filter = ResourceGroupFilters.Search(query.TenantId, query.Search);
        var totalCount = await resourceGroups.CountDocumentsAsync(
            filter,
            cancellationToken: cancellationToken);
        var items = await resourceGroups
            .Find(filter)
            .Skip(checked((query.PageNumber - 1) * query.PageSize))
            .Limit(query.PageSize)
            .ToListAsync(cancellationToken);
        return new ResourceGroupPage(items, totalCount);
    }
}
